using System.Windows.Forms;
using HighlightedItems.Utils;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore.Shared.Enums;
using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ItemFilterLibrary;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace HighlightedItems;

public class HighlightedItems : BaseSettingsPlugin<Settings>
{
    private SyncTask<bool> _currentOperation;
    private string _customStashFilter = "";
    private string _customInventoryFilter = "";

    private record QueryOrException(ItemQuery Query, Exception Exception);

    private readonly ConditionalWeakTable<string, QueryOrException> _queries = [];

    private bool MoveCancellationRequested =>
        Settings.CancelWithRightMouseButton && (Control.MouseButtons & MouseButtons.Right) != 0;

    private IngameState InGameState => GameController.IngameState;
    private SharpDX.Vector2 WindowOffset => GameController.Window.GetWindowRectangleTimeCache.TopLeft;

    public override bool Initialise()
    {
        Graphics.InitImage(Path.Combine(DirectoryFullName, "images\\pick.png").Replace('\\', '/'), false);
        Graphics.InitImage(Path.Combine(DirectoryFullName, "images\\pickL.png").Replace('\\', '/'), false);

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _mouseStateForRect.Clear();
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        DrawIgnoredCellsSettings();
    }

    private Predicate<Entity> GetPredicate(string windowTitle, ref string filterText, Vector2 defaultPosition)
    {
        bool isInventoryFilter = windowTitle.Contains("inventory");
        if (!Settings.ShowCustomFilterWindowStash && !isInventoryFilter
            || !Settings.ShowCustomFilterWindowInventory && isInventoryFilter) return null;
        Settings.SavedFilters ??= [];
        ImGui.SetNextWindowPos(defaultPosition, ImGuiCond.FirstUseEver);
        if (ImGui.Begin(windowTitle, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.InputTextWithHint("##input", "Filter using IFL syntax", ref filterText, 2000);
            Predicate<Entity> returnValue = null;
            if (!string.IsNullOrWhiteSpace(filterText))
            {
                ImGui.SameLine();
                if (ImGui.Button("Clear"))
                {
                    filterText = "";
                    return null;
                }

                if (!Settings.SavedFilters.Contains(filterText))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Save"))
                    {
                        Settings.SavedFilters.Add(filterText);
                    }
                }

                var (query, exception) = _queries.GetValue(filterText, s =>
                {
                    try
                    {
                        var itemQuery = ItemQuery.Load(s);
                        if (itemQuery.FailedToCompile)
                        {
                            return new QueryOrException(null, new Exception(itemQuery.Error));
                        }

                        return new QueryOrException(itemQuery, null);
                    }
                    catch (Exception ex)
                    {
                        return new QueryOrException(null, ex);
                    }
                })!;

                if (exception != null)
                {
                    ImGui.TextUnformatted($"{exception.Message}");
                }
                else
                {
                    returnValue = s =>
                    {
                        try
                        {
                            return query.CompiledQuery(new ItemData(s, GameController));
                        }
                        catch (Exception ex)
                        {
                            DebugWindow.LogError($"Failed to match item: {ex}");
                            return false;
                        }
                    };
                }
            }

            // ReSharper disable once AssignmentInConditionalExpression
            if (Settings.SavedFilters.Any() && Settings.UsePopupForFilterSelector
                    ? Settings.OpenSavedFilterList = ImGui.BeginPopupContextItem("saved_filter_popup")
                    : Settings.OpenSavedFilterList = ImGui.TreeNodeEx("Saved filters",
                        Settings.OpenSavedFilterList
                            ? ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.NoTreePushOnOpen
                            : ImGuiTreeNodeFlags.NoTreePushOnOpen))
            {
                foreach (var (savedFilter, index) in Settings.SavedFilters.Select((x, i) => (x, i)).ToList())
                {
                    int maxLength = 40; // Set the maximum length to display
                    string displayText = savedFilter.Length > maxLength
                        ? savedFilter.Substring(0, maxLength) + "..."
                        : savedFilter;
                    ImGui.PushID($"saved{index}");
                    if (ImGui.Button("Load"))
                    {
                        filterText = savedFilter;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(savedFilter); // Show the full text as a tooltip
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Delete"))
                    {
                        if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                        {
                            Settings.SavedFilters.Remove(savedFilter);
                        }
                    }
                    else if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Hold Shift");
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(displayText);

                    ImGui.PopID();
                }

                if (Settings.UsePopupForFilterSelector)
                {
                    ImGui.EndPopup();
                }
                else
                {
                    ImGui.TreePop();
                }
            }

            if (Settings.UsePopupForFilterSelector)
            {
                if (ImGui.Button("Open Saved Filters"))
                {
                    ImGui.OpenPopup("saved_filter_popup");
                }
            }

            ImGui.End();
            return returnValue;
        }

        return null;
    }

    public bool IsAnythingHighlighted = false;
    public bool ShouldUnholdShift = false;

    public override void Render()
    {
        if (_currentOperation != null)
        {
            DebugWindow.LogMsg("Running the inventory dump procedure...");
            TaskUtils.RunOrRestart(ref _currentOperation, () => null);
            /*if (_itemsToMove is { Count: > 0 } itemsToMove)
            {
                foreach (var (rect, color) in itemsToMove.Skip(1).Select(x => (x, Settings.CustomFilterFrameColor))
                             .Prepend((itemsToMove[0], Color.Green)))
                {
                    Graphics.DrawFrame(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(), color,
                        Settings.CustomFilterFrameThickness);
                }
            }*/
            if (Input.IsKeyDown(Keys.LShiftKey) && _currentOperation is null)
            {
                ShouldUnholdShift = true;
            }
            //return;
        }

        if (ShouldUnholdShift && (!Settings.HoldShiftForMapsIfNecessary.Value || !IsAnythingHighlighted))
        {
            ShouldUnholdShift = false;
            DebugWindow.LogMsg($"Shift up");
            Keyboard.KeyUp(Keys.LShiftKey);
        }


        if (!Settings.Enable)
            return;

        var (inventory, rectElement, highlightText) =
            (InGameState.IngameUi.StashElement, InGameState.IngameUi.GuildStashElement, InGameState.IngameUi.PurchaseWindow) switch
            {
                ({
                    IsVisible: true,
                    VisibleStash: { InventoryUIElement: { } invRect } visibleStash,
                    Children: var children
                }, _,_) => (visibleStash, invRect, children[3].Children[1].Children[0].Text),
                (_, {
                    IsVisible: true,
                    VisibleStash: { InventoryUIElement: { } invRect } visibleStash,
                    Children: var children
                },_) => (visibleStash, invRect, children[3].Children[1].Children[0].Text),
                (_,_,{
                    IsVisible: true,
                    TabContainer: { VisibleStash: { InventoryUIElement: { } invRect } visibleStash } purchaseWindow,
                    Children: var children
                }) => (visibleStash, invRect, children[4].Children[1].Children[0].Text),
                _ => (null, null, null)
            };

        const float buttonSize = 37;
        var highlightedItemsFound = false;
        if (inventory != null)
        {
            var isTextSet = !string.IsNullOrEmpty(highlightText);
            var stashRect = rectElement.GetClientRectCache;
            var (itemFilter, isCustomFilter) =
                GetPredicate("Custom stash filter", ref _customStashFilter, stashRect.BottomLeft.ToVector2Num()) is
                    { } customPredicate
                    ? (
                        (Predicate<NormalInventoryItem>)(s =>
                            (!isTextSet || s.isHighlighted != Settings.InvertSelection.Value) &&
                            customPredicate(s.Item)), true)
                    : (s => s.isHighlighted != Settings.InvertSelection.Value, false);

            //Determine Stash Pickup Button position and draw
            var buttonPos = Settings.UseCustomMoveToInventoryButtonPosition
                ? Settings.CustomMoveToInventoryButtonPosition
                : stashRect.BottomRight.ToVector2Num() + new Vector2(-43, 10);
            var buttonRect = new SharpDX.RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);

            Graphics.DrawImage("pick.png", buttonRect);

            var highlightedItems = GetHighlightedItems(inventory, itemFilter);
            highlightedItemsFound = highlightedItems.Any();
            int? stackSizes = 0;

            if (isCustomFilter && isTextSet)
            {
                foreach (var item in inventory.VisibleInventoryItems.Where(i =>
                             i.isHighlighted && !highlightedItems.Contains(i)))
                {
                    var rect = item.GetClientRectCache;
                    Graphics.DrawBox(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(), Color.Black);
                }
            }

            foreach (var item in highlightedItems)
            {
                stackSizes += item.Item?.GetComponent<Stack>()?.Size;
                if (isCustomFilter)
                {
                    var rect = item.GetClientRectCache;
                    var deflateFactor = Settings.CustomFilterBorderDeflation / 200.0;
                    var deflateWidth = (int)(rect.Width * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                    var deflateHeight = (int)(rect.Height * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                    rect.Inflate(-deflateWidth, -deflateHeight);

                    var topLeft = rect.TopLeft.ToVector2Num();
                    var bottomRight = rect.BottomRight.ToVector2Num();
                    Graphics.DrawFrame(topLeft, bottomRight, Settings.CustomFilterFrameColor,
                        Settings.CustomFilterBorderRounding, Settings.CustomFilterFrameThickness, 0);
                }
            }

            var countText = Settings.ShowStackSizes && highlightedItems.Count != stackSizes && stackSizes != null
                ? Settings.ShowStackCountWithSize
                    ? $"{stackSizes} / {highlightedItems.Count}"
                    : $"{stackSizes}"
                : $"{highlightedItems.Count}";

            var countPos = new Vector2(buttonRect.Left - 2, buttonRect.Center.Y - 11);
            Graphics.DrawText($"{countText}", countPos with { Y = countPos.Y + 2 }, SharpDX.Color.Black,
                FontAlign.Right);
            Graphics.DrawText($"{countText}", countPos with { X = countPos.X - 2 }, SharpDX.Color.White,
                FontAlign.Right);

            if
                ( /*IsAnythingHighlighted&&Settings.ContinueUseOrbsWhileHighlighted.Value&&_currentOperation is null&&GameController.IngameState.IngameUi.Cursor.Action is MouseActionType.UseItem||*/
                 IsButtonPressed(buttonRect) ||
                 Input.IsKeyDown(Settings.MoveToInventoryHotkey.Value))
            {
                var orderedItems = highlightedItems
                    .OrderBy(stashItem => stashItem.GetClientRectCache.X)
                    .ThenBy(stashItem => stashItem.GetClientRectCache.Y)
                    .ToList();
                _currentOperation = MoveItemsToInventory(orderedItems);
            }
        }
        else
        {
            if (Settings.ResetCustomFilterOnPanelClose)
            {
                _customStashFilter = "";
            }
        }

        IsAnythingHighlighted = highlightedItemsFound;

        var inventoryPanel = InGameState.IngameUi.InventoryPanel;
        if (inventoryPanel.IsVisible)
        {
            var inventoryRect = inventoryPanel[2].GetClientRectCache;

            var (itemFilter, isCustomFilter) = GetPredicate("Custom inventory filter", ref _customInventoryFilter,
                inventoryRect.BottomLeft.ToVector2Num()) is { } customPredicate
                ? (
                    (Predicate<NormalInventoryItem>)(s =>
                        s.IsSaturated && customPredicate(s.Item)), true)
                : (s => s.IsSaturated, false);

            if (Settings.DumpButtonEnable && IsStashTargetOpened)
            {
                //Determine Inventory Pickup Button position and draw
                var buttonPos = Settings.UseCustomMoveToStashButtonPosition
                    ? Settings.CustomMoveToStashButtonPosition
                    : inventoryRect.TopLeft.ToVector2Num() + new Vector2(buttonSize / 2, -buttonSize);
                var buttonRect = new SharpDX.RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);

                if (isCustomFilter)
                {
                    foreach (var item in GameController.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].VisibleInventoryItems
                                 .Where(x => itemFilter(x)))
                    {
                        var rect = item.GetClientRect();
                        Graphics.DrawFrame(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(),
                            Settings.CustomFilterFrameColor, Settings.CustomFilterFrameThickness);
                    }
                }

                Graphics.DrawImage("pickL.png", buttonRect);
                if (IsButtonPressed(buttonRect) ||
                    Input.IsKeyDown(Settings.MoveToStashHotkey.Value) ||
                    Settings.UseMoveToInventoryAsMoveToStashWhenNoHighlights &&
                    !highlightedItemsFound &&
                    Input.IsKeyDown(Settings.MoveToInventoryHotkey.Value))
                {
                    var inventoryItems = GameController.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].VisibleInventoryItems
                        .Where(x => !IsInIgnoreCell(x))
                        .Where(x => itemFilter(x))
                        .OrderBy(x => x.GetClientRect().X)
                        .ThenBy(x => x.GetClientRect().Y)
                        .ToList();
                    DebugWindow.LogMsg($"Moving {inventoryItems.Count} items to stash");

                    _currentOperation = MoveItemsToStash(inventoryItems);
                }
            }
        }
        else
        {
            if (Settings.ResetCustomFilterOnPanelClose)
            {
                _customInventoryFilter = "";
            }
        }
    }

    private async SyncTask<bool> MoveItemsCommonPreamble()
    {
        while (Control.MouseButtons == MouseButtons.Left || MoveCancellationRequested)
        {
            if (MoveCancellationRequested)
            {
                return false;
            }

            await TaskUtils.NextFrame();
        }

        if (Settings.IdleMouseDelay.Value == 0)
        {
            return true;
        }

        var mousePos = Mouse.GetCursorPosition();
        var sw = Stopwatch.StartNew();
        await TaskUtils.NextFrame();
        while (true)
        {
            if (MoveCancellationRequested)
            {
                return false;
            }

            var newPos = Mouse.GetCursorPosition();
            if (mousePos != newPos)
            {
                mousePos = newPos;
                sw.Restart();
            }
            else if (sw.ElapsedMilliseconds >= Settings.IdleMouseDelay.Value)
            {
                return true;
            }
            else
            {
                await TaskUtils.NextFrame();
            }
        }
    }

    private async SyncTask<bool> MoveItemsToStash(List<NormalInventoryItem> items)
    {
        if (!await MoveItemsCommonPreamble())
        {
            return false;
        }

        var prevMousePos = Mouse.GetCursorPosition();
        var itemLocationIdPairs = items.Select(i => (i.GetClientRect().Center,i.Item.Id)).ToList();
        Keyboard.KeyDown(Keys.LControlKey);
        await Wait(KeyDelay, true);
        for (var i = 0; i < itemLocationIdPairs.Count; i++)
        {
            var itemLocationId = itemLocationIdPairs[i];
            // _itemsToMove = items[i..].Select(x => x.GetClientRect()).ToList();
            if (MoveCancellationRequested)
            {
                //  _itemsToMove = null;
                Keyboard.KeyUp(Keys.LControlKey);
                await Wait(KeyDelay, false);
                return false;
            }

            if (!InGameState.IngameUi.InventoryPanel.IsVisible)
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory Panel closed, aborting loop");
                break;
            }

            if (!IsStashTargetOpened)
            {
                DebugWindow.LogMsg("HighlightedItems: Target inventory closed, aborting loop");
                break;
            }

            /*if (Settings.DelayBetweenNItems.Value != 0 && i > 0 && i % Settings.DelayBetweenNItems.Value == 0
                && GameController.IngameState.IngameUi.Cursor.ActionString is "corrupt_item" or "identify")
            {
                await Wait(TimeSpan.FromMilliseconds(Settings.DelayTime.Value), false);
            }*/

            await MoveItem(itemLocationId.Center,itemLocationId.Id);
        }

        Mouse.moveMouse(prevMousePos);
        Keyboard.KeyUp(Keys.LControlKey);
        await Wait(KeyDelay, false);
        //_itemsToMove = null;
        return true;
    }

    private bool IsStashTargetOpened =>
        !Settings.VerifyTargetInventoryIsOpened
        || InGameState.IngameUi.StashElement.IsVisible
        || InGameState.IngameUi.SellWindow.IsVisible
        || InGameState.IngameUi.SellWindowHideout.IsVisible
        || InGameState.IngameUi.TradeWindow.IsVisible
        || InGameState.IngameUi.GuildStashElement.IsVisible;

    private bool IsStashSourceOpened =>
        !Settings.VerifyTargetInventoryIsOpened
        || InGameState.IngameUi.StashElement.IsVisible
        || InGameState.IngameUi.GuildStashElement.IsVisible
        || InGameState.IngameUi.PurchaseWindow.IsVisible;

    //private List<RectangleF> _itemsToMove = null;

    private async SyncTask<bool> MoveItemsToInventory(List<NormalInventoryItem> items)
    {
        if (!await MoveItemsCommonPreamble())
        {
            return false;
        }

        var prevMousePos = Mouse.GetCursorPosition();
        var itemLocationIdPairs = items.Select(i => (i.GetClientRect().Center,i.Item.Id)).ToList();
        Keyboard.KeyDown(Keys.LControlKey);
        await Wait(KeyDelay, true);
        for (var i = 0; i < itemLocationIdPairs.Count; i++)
        {
            var itemLocationId = itemLocationIdPairs[i];
            //_itemsToMove = items[i..].Select(x => x.GetClientRectCache).ToList();
            if (MoveCancellationRequested)
            {
                //_itemsToMove = null;
                Keyboard.KeyUp(Keys.LControlKey);
                await Wait(KeyDelay, false);
                return false;
            }

            if (!IsStashSourceOpened)
            {
                DebugWindow.LogMsg("HighlightedItems: Stash Panel closed, aborting loop");
                break;
            }

            if (!InGameState.IngameUi.InventoryPanel.IsVisible)
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory Panel closed, aborting loop");
                break;
            }

            if (IsInventoryFull())
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory full, aborting loop");
                break;
            }

            if (Settings.DelayBetweenNItems.Value != 0 && i > 0 && i % Settings.DelayBetweenNItems.Value == 0
                && GameController.IngameState.IngameUi.Cursor.ActionString is "corrupt_item" or "identify")
            {
                await Wait(TimeSpan.FromMilliseconds(Settings.DelayTime.Value), false);
            }


            await MoveItem(itemLocationId.Center,itemLocationId.Id);
        }

        Keyboard.KeyUp(Keys.LControlKey);
        await Wait(KeyDelay, false);
        Mouse.moveMouse(prevMousePos);
        //_itemsToMove = null;
        return true;
    }

    private List<NormalInventoryItem> GetHighlightedItems(Inventory stash, Predicate<NormalInventoryItem> filter)
    {
        try
        {
            var stashItems = stash.VisibleInventoryItems;

            var highlightedItems = stashItems
                .Where(stashItem => filter(stashItem))
                .ToList();

            return highlightedItems;
        }
        catch
        {
            return [];
        }
    }

    private bool IsInventoryFull()
    {
        var inventoryItems = GameController.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].ServerInventory.InventorySlotItems;

        // quick sanity check
        if (inventoryItems.Count < 12)
        {
            return false;
        }

        // track each inventory slot
        bool[,] inventorySlot = new bool[12, 5];

        // iterate through each item in the inventory and mark used slots
        foreach (var inventoryItem in inventoryItems)
        {
            int x = inventoryItem.PosX;
            int y = inventoryItem.PosY;
            int height = inventoryItem.SizeY;
            int width = inventoryItem.SizeX;
            for (int row = x; row < x + width; row++)
            {
                for (int col = y; col < y + height; col++)
                {
                    inventorySlot[row, col] = true;
                }
            }
        }

        // check for any empty slots
        for (int x = 0; x < 12; x++)
        {
            for (int y = 0; y < 5; y++)
            {
                if (inventorySlot[x, y] == false)
                {
                    return false;
                }
            }
        }

        // no empty slots, so inventory is full
        return true;
    }

    private static readonly TimeSpan KeyDelay = TimeSpan.FromMilliseconds(10);
    private TimeSpan ExtraDelay => TimeSpan.FromMilliseconds(Settings.ExtraDelay.Value);
    private TimeSpan MouseDownDelay => TimeSpan.FromMilliseconds(Settings.MouseDownDelay.Value);
    //private TimeSpan MouseUpDelay => TimeSpan.FromMilliseconds(Settings.MouseUpDelay.Value);

    private async SyncTask<bool> MoveItem(SharpDX.Vector2 itemPosition, uint id)
    {
        itemPosition += WindowOffset;
        if (!Input.IsKeyDown(Keys.LShiftKey))
        {
            Keyboard.KeyDown(Keys.LControlKey);
            await Wait(KeyDelay, true);
        }

        await HoverEntity(itemPosition, id);
        await Wait(ExtraDelay, true);
        Mouse.LeftDown();
        await Wait(MouseDownDelay, true);
        Mouse.LeftUp();
        await Wait(ExtraDelay, true);
       
        return true;
    }

    private async SyncTask<bool> Wait(TimeSpan period, bool canUseThreadSleep)
    {
        if (canUseThreadSleep && Settings.UseThreadSleep&&period.TotalMilliseconds>0)
        {
            Thread.Sleep(period);
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < period)
        {
            await TaskUtils.NextFrame();
        }

        return true;
    }
    
    /*private async SyncTask<bool> HoverElement(SharpDX.Vector2 itemPosition,long address)
    {
        Mouse.moveMouse(itemPosition);
        var isHoverCorrect = false;
        var retryCount = 4;
        var uiHoverElement = GameController.IngameState.UIHoverElement;
        for (int i = 0; i < retryCount; i++)
        {
            if (uiHoverElement?.Entity != null && uiHoverElement.Address == address)
            {
                isHoverCorrect = true;
                break;
            }
    
            Mouse.moveMouse(itemPosition);
            await TaskUtils.NextFrame();
            uiHoverElement = GameController.IngameState.UIHoverElement;
        }
        
        return isHoverCorrect;
    }*/
    
    private async SyncTask<bool> HoverEntity(SharpDX.Vector2 itemPosition,uint id)
    {
        Mouse.moveMouse(itemPosition);
        var isHoverCorrect = false;
        var retryCount = 4;
        var uiHoverElement = GameController.IngameState.UIHoverElement;
        for (int i = 0; i < retryCount; i++)
        {
            if (uiHoverElement?.Entity != null && uiHoverElement.Entity.Id == id)
            {
                isHoverCorrect = true;
                break;
            }
    
            Mouse.moveMouse(itemPosition);
            await TaskUtils.NextFrame();
            uiHoverElement = GameController.IngameState.UIHoverElement;
        }
        
        return isHoverCorrect;
    }

    private readonly ConcurrentDictionary<RectangleF, bool?> _mouseStateForRect = [];

    private bool IsButtonPressed(RectangleF buttonRect)
    {
        var prevState = _mouseStateForRect.GetValueOrDefault(buttonRect);
        var isHovered = buttonRect.Contains(Mouse.GetCursorPosition() - WindowOffset);
        if (!isHovered)
        {
            _mouseStateForRect[buttonRect] = null;
            return false;
        }

        var isPressed = Control.MouseButtons == MouseButtons.Left && CanClickButtons;
        _mouseStateForRect[buttonRect] = isPressed;
        if (isPressed && GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.UseItem &&
            !Input.IsKeyDown(Keys.LShiftKey))
        {
            DebugWindow.LogMsg("Shift down");
            Keyboard.KeyDown(Keys.LShiftKey);
        }

        return isPressed &&
               prevState == false;
    }

    private bool CanClickButtons => !Settings.VerifyButtonIsNotObstructed || !ImGui.GetIO().WantCaptureMouse;

    private bool IsInIgnoreCell(NormalInventoryItem inventItem)
    {
        var inventPosX = (int)(inventItem.X / inventItem.Width);
        var inventPosY = (int)(inventItem.Y / inventItem.Height);

        if (inventPosX < 0 || inventPosX >= 12)
            return true;
        if (inventPosY < 0 || inventPosY >= 5)
            return true;

        return Settings.IgnoredCells[inventPosY, inventPosX]; //No need to check all item size
    }

    private void DrawIgnoredCellsSettings()
    {
        ImGui.BeginChild("##IgnoredCellsMain", new Vector2(ImGui.GetContentRegionAvail().X, 204f),
            ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.Text("Ignored Inventory Slots (checked = ignored)");

        var contentRegionAvail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("##IgnoredCellsCels", new Vector2(contentRegionAvail.X, contentRegionAvail.Y),
            ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollWithMouse);

        for (int y = 0; y < 5; ++y)
        {
            for (int x = 0; x < 12; ++x)
            {
                bool isCellIgnored = Settings.IgnoredCells[y, x];
                if (ImGui.Checkbox($"##{y}_{x}IgnoredCells", ref isCellIgnored))
                    Settings.IgnoredCells[y, x] = isCellIgnored;
                if (x < 11)
                    ImGui.SameLine();
            }
        }

        ImGui.EndChild();
        ImGui.EndChild();
    }
}