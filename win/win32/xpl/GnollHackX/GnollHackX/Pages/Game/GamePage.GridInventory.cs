using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using GnollHackX;
using SkiaSharp;

#if GNH_MAUI
using SkiaSharp.Views.Maui;

namespace GnollHackM
#else
using SkiaSharp.Views.Forms;
using Xamarin.Essentials;

namespace GnollHackX.Pages.Game
#endif
{
    /* Grid inventory in the menu canvas: draws the inventory menu with GHGridInventoryView and sends its actions to
       the C core as the picked count of the selected item (GHConstants.InventoryActionCode). The item command menu
       that follows a right click is drawn as a popup at the pointer. Paint and touch both run on the UI thread. */
    public partial class GamePage : IGHGridInventoryHost
    {
        private const long GridContextPendingMilliseconds = 3000;

        private int _gridInventoryShown = 0;
        private int _gridContextPopupShown = 0;
        private bool GridInventoryShown { get { return Interlocked.CompareExchange(ref _gridInventoryShown, 0, 0) != 0; } set { Interlocked.Exchange(ref _gridInventoryShown, value ? 1 : 0); } }
        private bool GridContextPopupShown { get { return Interlocked.CompareExchange(ref _gridContextPopupShown, 0, 0) != 0; } set { Interlocked.Exchange(ref _gridContextPopupShown, value ? 1 : 0); } }

        /* True if the menu canvas is drawn and handled by the grid inventory; a gamepad uses the list */
        private bool GridInventoryPaintActive { get { return (GridInventoryShown || GridContextPopupShown) && !GHGamepadService.IsActive; } }

        /* The equipment slots of the dock, shared with the inventory window */
        public static EquipmentSlot[] EquipmentSlotList { get { return _equipmentSlots; } }

        private GHGridInventoryView _gridView;
        private GHGridInventoryView GridView
        {
            get
            {
                if (_gridView == null)
                {
                    _gridView = new GHGridInventoryView(this, GHGridViewMode.Inventory, _equipmentSlots);
                    _gridView.PopOutClicked += GridView_PopOutClicked;
                }
                return _gridView;
            }
        }

        private bool _gridContextPending = false;
        private SKPoint _gridContextPoint;
        private DateTime _gridContextPendingTime = DateTime.MinValue;
        private IList<GHMenuItem> _gridBackdropItems = null;
        private string _gridBackdropTitle = null;

        /* Called when a menu is shown: decides whether it is drawn as the grid or as the command popup */
        private void UpdateGridInventoryModeForMenu(ghmenu_styles style, SelectionMode how)
        {
            bool grid = GHApp.InventoryGridMode && how == SelectionMode.Single
                && (style == ghmenu_styles.GHMENU_STYLE_INVENTORY || style == ghmenu_styles.GHMENU_STYLE_INVENTORY_EQUIPMENT);
            bool pendingValid = _gridContextPending
                && (DateTime.Now - _gridContextPendingTime).TotalMilliseconds <= GridContextPendingMilliseconds;
            bool popup = !grid && pendingValid && style == ghmenu_styles.GHMENU_STYLE_ITEM_COMMAND && how == SelectionMode.Single;

            _gridContextPending = false;
            GHGridInventoryView view = GridView;
            view.ResetPress();
            view.ShowPopOutButton = GHGuiWindows.IsSupported && GHApp.SeparateGuiWindows;
            if (popup)
            {
                view.Items = _gridBackdropItems;
                view.ShowPopup(MenuCanvas.MenuItems, _gridContextPoint, _gridBackdropTitle);
            }
            else
            {
                view.ClosePopup();
                _gridBackdropItems = null;
                _gridBackdropTitle = null;
            }

            GridInventoryShown = grid;
            GridContextPopupShown = popup;

            if (grid)
            {
                /* The equipment slots are part of the grid view */
                MenuEquipmentSideShown = false;
                MenuFlipButton.IsEnabled = false;
                MenuFlipButton.IsVisible = false;
            }
        }

        private void PaintGridInventory(SKCanvas canvas, float canvaswidth, float canvasheight, ObservableCollection<GHMenuItem> menuItems)
        {
            GHGridInventoryView view = GridView;
            if (!view.IsPopupShown)
                view.Items = menuItems;
            lock (_menuHoverLock)
            {
                if (!view.IsDragging)
                    view.SetHover(_menuHoverPoint, _menuIsHovering);
            }
            view.Paint(canvas, canvaswidth, canvasheight);
        }

        private void HandleGridInventoryTouch(object sender, SKTouchEventArgs e)
        {
            GridView.HandleTouch(e);
        }

        /* Sends the item at menuIdx as the menu's selection with count, which is -1 or an inventory action code */
        private void SendGridSelection(int menuIdx, int count)
        {
            var menuItems = MenuCanvas.MenuItems;
            if (menuItems == null || menuIdx < 0 || menuIdx >= menuItems.Count)
                return;
            int oldSel = MenuCanvas.SelectionIndex;
            if (oldSel >= 0 && oldSel < menuItems.Count && oldSel != menuIdx)
                menuItems[oldSel].Count = 0;
            MenuCanvas.SelectionIndex = menuIdx;
            menuItems[menuIdx].Count = count;
            GridView.ResetPress();
            MenuCanvas.InvalidateSurface();
            PressMenuOKButton();
        }

        /* The command popup is shown at p for the next item command menu */
        private void SetGridContextPending(SKPoint p, GHMenuItem mi)
        {
            _gridContextPending = true;
            _gridContextPoint = p;
            _gridContextPendingTime = DateTime.Now;
            _gridBackdropItems = MenuCanvas.MenuItems;
            _gridBackdropTitle = mi?.TrimmedMainText;
        }

        private void GridView_PopOutClicked(object sender, EventArgs e)
        {
            /* The inventory moves to its own window; the menu here closes */
            GHGuiWindows.OpenInventoryWindow(GHApp.CurrentGHGame);
            PressMenuCancelButton();
        }

        /*
         * IGHGridInventoryHost
         */

        bool IGHGridInventoryHost.UseGL { get { return MenuCanvas.UseGL; } }
        bool IGHGridInventoryHost.HighFilterQuality { get { return MenuHighFilterQuality; } }
        bool IGHGridInventoryHost.IsTwoWeap { get { return MenuIsTwoWeap; } }

        void IGHGridInventoryHost.InvalidateGrid()
        {
            MenuCanvas.InvalidateSurface();
        }

        void IGHGridInventoryHost.OnGridAction(GHGridInventoryView view, int menuIdx, int action, int target, SKPoint point)
        {
            GHMenuItem mi = view.GetMenuItem(menuIdx);
            if (action == GHConstants.INVENTORY_ACTION_MENU)
            {
                SetGridContextPending(point, mi);
                SendGridSelection(menuIdx, -1);
                return;
            }
            if (action == GHConstants.INVENTORY_ACTION_DEFAULT)
                SetGridContextPending(point, mi); /* If there is no default action, the item command menu comes up as the popup */
            SendGridSelection(menuIdx, GHConstants.InventoryActionCode(action, target));
        }

        void IGHGridInventoryHost.OnPopupRowSelected(GHGridInventoryView view, int menuIdx)
        {
            SendGridSelection(menuIdx, -1);
        }

        void IGHGridInventoryHost.OnPopupCancelled(GHGridInventoryView view)
        {
            /* Back to the inventory */
            PressMenuCancelButton();
        }

        bool IGHGridInventoryHost.OnDragReleasedOutside(GHGridInventoryView view, int menuIdx, GHGridHitKind sourceKind)
        {
            GHMenuItem mi = view.GetMenuItem(menuIdx);
            if (mi == null)
                return false;
            /* Onto a container window: put in */
            uint containerOid;
            if (sourceKind == GHGridHitKind.Cell && GHGuiWindows.FindContainerWindowUnderCursor(out containerOid))
            {
                char containerLetter = GHGuiWindows.GetContainerLetter(containerOid);
                if (containerLetter != '\0' && containerLetter != (char)mi.Identifier)
                {
                    SendGridSelection(menuIdx, GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_STASH, containerLetter));
                    return true;
                }
                return false;
            }
            /* Elsewhere outside the menu: drop, as in the menu's empty areas */
            SendGridSelection(menuIdx, GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_DROP, 0));
            return true;
        }
    }
}
