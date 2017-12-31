﻿using PoeHUD.Models;
using PoeHUD.Plugins;
using PoeHUD.Poe.RemoteMemoryObjects;
using System.Collections.Generic;
using System.Security.Cryptography;
using PoeHUD.Poe.Elements;
using System.Windows.Forms;
using HighlightedItems.Utils;
using System.Threading;
using SharpDX;
using PoeHUD.Framework;

namespace HighlightedItems
{
    internal class HighlightedItems : BaseSettingsPlugin<Settings>
    {
        private readonly IngameState ingameState;
        private bool isBusy = false;
        private MD5 md5Hasher = MD5.Create();


        public HighlightedItems()
        {
            ingameState = GameController.Game.IngameState;
            PluginName = "HighlightedItems";
        }

        public override void Initialise()
        {
            base.Initialise();
        }

        public override void Render()
        {
            if (!Settings.Enable)
                return;

            if (ingameState.ServerData.StashPanel.IsVisible)
            {

                var stashRect = ingameState.ServerData.StashPanel.VisibleStash.InventoryUiElement.GetClientRect();
                var pickButtonRect = new RectangleF(stashRect.BottomRight.X - 43, stashRect.BottomRight.Y + 10, 37, 37);


                Graphics.DrawPluginImage(PluginDirectory+"\\images\\pick.png", pickButtonRect);

                if (Control.MouseButtons == MouseButtons.Left)
                {
                    var prevMousePos = Mouse.GetCursorPosition();

                    if (pickButtonRect.Contains(Mouse.GetCursorPosition()))
                    {
                        isBusy = true;
                        GetHighlightedItems();
                        isBusy = false;
                    }
                    Mouse.moveMouse(prevMousePos);

                }
                    if (WinApi.IsKeyDown(Settings.HotKey) && !isBusy)
                {
                    isBusy = true;
                    GetHighlightedItems();
                    isBusy = false;
                }
            }

        }

        public override void EntityAdded(EntityWrapper entityWrapper)
        {
            base.EntityAdded(entityWrapper);
        }

        public override void EntityRemoved(EntityWrapper entityWrapper)
        {
            base.EntityRemoved(entityWrapper);
        }

        public override void OnClose()
        {

            base.OnClose();
        }

        
        public void GetHighlightedItems()
        {
            var inventoryItems = ingameState.ServerData.StashPanel.VisibleStash.VisibleInventoryItems;
            foreach (var item in inventoryItems)
            {
                if (item.isHighlighted)
                {
                    moveItem(item.GetClientRect().Center);
                }
            }
        }


        public void moveItem(Vector2 itemPosition)
        {
            Keyboard.HoldKey((byte)Keys.LControlKey);
            Thread.Sleep(Mouse.DELAY_MOVE);
            Mouse.moveMouse(itemPosition);
            Mouse.LeftUp(Settings.Speed);
            Thread.Sleep(Mouse.DELAY_MOVE);
            Keyboard.ReleaseKey((byte)Keys.LControlKey);
        }

        private void debug()
        {
        }
    }
}