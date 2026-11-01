using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SkiaSharp;

#if GNH_MAUI
using GnollHackM;
#if WINDOWS && !GNH_AVALONIA
using System.Runtime.InteropServices;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
#endif
#endif

namespace GnollHackX
{
    /* Separate windows for the inventory, carried containers and Gnoll Overseer, on WinUI only. The core sends the
       inventory and the contents of the open containers as menus that are never selected (PublishMenu), says when it
       waits for a command (NotifyAwaitingCommand), and opens and closes container windows; the windows send their
       actions back as the #guiaction command (GHGame.SendGuiAction). The item command menu asked for by a window is
       drawn as a popup in that window. Everything except the entry points marked as such runs on the UI thread. */
    public static class GHGuiWindows
    {
#if GNH_MAUI && WINDOWS && !GNH_AVALONIA
        public static bool IsSupported { get { return true; } }
#else
        public static bool IsSupported { get { return false; } }
#endif
        public static bool IsEnabled { get { return IsSupported && GHApp.SeparateGuiWindows; } }

#if GNH_MAUI && WINDOWS && !GNH_AVALONIA
        private const long PopupPendingMilliseconds = 3000;
        private const long DimDelayMilliseconds = 350;
        private const string InventoryWindowOpenKey = "GuiInventoryWindowOpen";

        private static GHGame _game;
        private static GHGame _configGame;
        private static DateTime _notAwaitingSince = DateTime.MinValue;
        private static bool _closingAll;
        private static readonly List<int[]> _pendingActions = new List<int[]>(); /* action, invlet, target, oid */

        private static Window _inventoryWindow;
        private static GHInventoryWindowPage _inventoryPage;
        private static List<GHMenuItem> _inventoryItems;
        private static readonly Dictionary<uint, Window> _containerWindows = new Dictionary<uint, Window>();
        private static readonly Dictionary<uint, GHContainerWindowPage> _containerPages = new Dictionary<uint, GHContainerWindowPage>();
        private static Window _overseerWindow;

        private static GHGridWindowPage _popupPage;
        private static SKPoint _popupPoint;
        private static string _popupTitle;
        private static DateTime _popupTime = DateTime.MinValue;
        private static GHGame _popupGame;
        private static GHPublishedWindow _popupWindow;
        private static GHGridWindowPage _popupShownPage;

        /* True while the core waits for a command, so that the windows can act */
        public static bool CanAct { get { GHGame game = _game; return game != null && game.AwaitingCommand; } }

        /* True if the windows should look disabled; short busy spells between commands are not shown */
        public static bool ShowAsBusy
        {
            get
            {
                return !CanAct && _game != null && (DateTime.Now - _notAwaitingSince).TotalMilliseconds >= DimDelayMilliseconds;
            }
        }

        private static void OnMainThread(Action action)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            });
        }

        private static void InvalidateAll()
        {
            _inventoryPage?.Invalidate();
            foreach (GHContainerWindowPage page in _containerPages.Values)
                page.Invalidate();
        }

        /* Game thread */
        public static void NotifyAwaitingCommand(GHGame game, bool awaiting)
        {
            if (!IsSupported)
                return;
            OnMainThread(() =>
            {
                if (game != _game)
                {
                    CloseContainerWindows();
                    _game = game;
                    _configGame = null;
                    _pendingActions.Clear();
                }
                if (!awaiting)
                {
                    _notAwaitingSince = DateTime.Now;
                    /* Shows the windows as busy if this lasts */
                    Task.Delay((int)DimDelayMilliseconds + 20).ContinueWith(t => OnMainThread(InvalidateAll));
                }
                else
                {
                    if (_configGame != game)
                    {
                        /* Tells a new game which windows are open */
                        _configGame = game;
                        _pendingActions.Clear();
                        _pendingActions.Add(new int[] { GHConstants.GUI_ACTION_SEPARATE_WINDOWS, 0, IsEnabled ? 1 : 0, 0 });
                        if (IsEnabled && _inventoryWindow == null && Preferences.Get(InventoryWindowOpenKey, false))
                            OpenInventoryWindow(game);
                        else
                            _pendingActions.Add(new int[] { GHConstants.GUI_ACTION_INVENTORY_WINDOW, 0, _inventoryWindow != null ? 1 : 0, 0 });
                    }
                    TrySendPending();
                }
                InvalidateAll();
            });
        }

        private static void TrySendPending()
        {
            GHGame game = _game;
            if (game == null || _pendingActions.Count == 0)
                return;
            int[] a = _pendingActions[0];
            if (game.SendGuiAction(a[0], a[1], a[2], unchecked((uint)a[3])))
                _pendingActions.RemoveAt(0);
        }

        /* Sends an action of a window; returns false if the game is busy. Window settings that cannot wait for the
           next command are queued instead. */
        public static bool SendAction(int action, int invlet, int target, uint oid, bool queueIfBusy = false)
        {
            GHGame game = _game ?? GHApp.CurrentGHGame;
            if (game == null)
                return false;
            if (_pendingActions.Count > 0 || queueIfBusy && !game.AwaitingCommand)
            {
                _pendingActions.Add(new int[] { action, invlet, target, unchecked((int)oid) });
                TrySendPending();
                return true;
            }
            if (game.SendGuiAction(action, invlet, target, oid))
            {
                _notAwaitingSince = DateTime.Now;
                InvalidateAll();
                return true;
            }
            return false;
        }

        /* Game thread */
        public static void PublishMenu(GHGame game, GHPublishedWindow window)
        {
            if (!IsSupported || window?.MenuInfo == null)
                return;
            OnMainThread(() =>
            {
                GHMenuInfo info = window.MenuInfo;
                if (info.Style == ghmenu_styles.GHMENU_STYLE_PERMANENT_INVENTORY)
                {
                    _inventoryItems = info.MenuItems;
                    _inventoryPage?.UpdateItems(info.MenuItems, info.Header, info.Subtitle);
                }
                else if (info.Style == ghmenu_styles.GHMENU_STYLE_CONTAINER_CONTENTS && window.ObjData != null)
                {
                    uint oid = window.ObjData.ObjData.o_id;
                    GHContainerWindowPage page;
                    if (_containerPages.TryGetValue(oid, out page))
                        page.UpdateContents(info.MenuItems, info.Header, info.Subtitle, (char)(byte)window.ObjData.ObjData.invlet);
                }
            });
        }

        /*
         * Window geometry
         */

        private static void RestoreGeometry(Window window, string name, double defaultWidth, double defaultHeight)
        {
            window.Width = Preferences.Get("GuiWindow" + name + "Width", defaultWidth);
            window.Height = Preferences.Get("GuiWindow" + name + "Height", defaultHeight);
            double x = Preferences.Get("GuiWindow" + name + "X", double.NaN);
            double y = Preferences.Get("GuiWindow" + name + "Y", double.NaN);
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                window.X = x;
                window.Y = y;
            }
        }

        private static void SaveGeometry(Window window, string name)
        {
            if (window.Width > 50 && window.Height > 50)
            {
                Preferences.Set("GuiWindow" + name + "Width", window.Width);
                Preferences.Set("GuiWindow" + name + "Height", window.Height);
                Preferences.Set("GuiWindow" + name + "X", window.X);
                Preferences.Set("GuiWindow" + name + "Y", window.Y);
            }
        }

        private static void Activate(Window window)
        {
            try
            {
                Application.Current?.ActivateWindow(window);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /*
         * Inventory window
         */

        public static void OpenInventoryWindow(GHGame game)
        {
            if (!IsEnabled)
                return;
            if (game != null && _game == null)
                _game = game;
            if (_inventoryWindow != null)
            {
                Activate(_inventoryWindow);
                return;
            }

            _inventoryPage = new GHInventoryWindowPage();
            if (_inventoryItems != null)
                _inventoryPage.UpdateItems(_inventoryItems, null, null);
            Window window = new Window(_inventoryPage) { Title = "GnollHack - Inventory" };
            RestoreGeometry(window, "Inventory", 1100, 600);
            window.Destroying += InventoryWindow_Destroying;
            window.Created += OwnedWindow_Created;
            _inventoryWindow = window;
            Application.Current?.OpenWindow(window);
            Preferences.Set(InventoryWindowOpenKey, true);
            SendAction(GHConstants.GUI_ACTION_INVENTORY_WINDOW, 0, 1, 0, true);
        }

        private static void InventoryWindow_Destroying(object sender, EventArgs e)
        {
            Window window = sender as Window;
            if (window == null)
                return;
            window.Destroying -= InventoryWindow_Destroying;
            SaveGeometry(window, "Inventory");
            if (window != _inventoryWindow)
                return;
            _inventoryWindow = null;
            _inventoryPage = null;
            if (!_closingAll)
            {
                Preferences.Set(InventoryWindowOpenKey, false);
                SendAction(GHConstants.GUI_ACTION_INVENTORY_WINDOW, 0, 0, 0, true);
            }
        }

        /*
         * Container windows
         */

        /* Game thread */
        public static void OpenContainerWindow(GHGame game, uint oid)
        {
            if (!IsSupported)
                return;
            OnMainThread(() =>
            {
                if (!IsEnabled)
                    return;
                Window existing;
                if (_containerWindows.TryGetValue(oid, out existing))
                {
                    Activate(existing);
                    return;
                }
                GHContainerWindowPage page = new GHContainerWindowPage(oid);
                Window window = new Window(page) { Title = "GnollHack - Container" };
                RestoreGeometry(window, "Container", 560, 520);
                window.Destroying += ContainerWindow_Destroying;
                window.Created += OwnedWindow_Created;
                _containerWindows[oid] = window;
                _containerPages[oid] = page;
                Application.Current?.OpenWindow(window);
            });
        }

        /* Game thread */
        public static void CloseContainerWindow(uint oid)
        {
            if (!IsSupported)
                return;
            OnMainThread(() =>
            {
                Window window;
                if (_containerWindows.TryGetValue(oid, out window))
                {
                    /* Closed by the core, so no GUI_ACTION_CLOSE_CONTAINER is sent */
                    _containerWindows.Remove(oid);
                    _containerPages.Remove(oid);
                    Application.Current?.CloseWindow(window);
                }
            });
        }

        private static void CloseContainerWindows()
        {
            List<Window> windows = new List<Window>(_containerWindows.Values);
            _containerWindows.Clear();
            _containerPages.Clear();
            foreach (Window window in windows)
                Application.Current?.CloseWindow(window);
        }

        private static void ContainerWindow_Destroying(object sender, EventArgs e)
        {
            Window window = sender as Window;
            if (window == null)
                return;
            window.Destroying -= ContainerWindow_Destroying;
            SaveGeometry(window, "Container");
            GHContainerWindowPage page = window.Page as GHContainerWindowPage;
            if (page != null && _containerWindows.ContainsKey(page.ContainerOid))
            {
                /* Closed by the player */
                _containerWindows.Remove(page.ContainerOid);
                _containerPages.Remove(page.ContainerOid);
                if (!_closingAll)
                    SendAction(GHConstants.GUI_ACTION_CLOSE_CONTAINER, 0, 0, page.ContainerOid, true);
            }
        }

        public static char GetContainerLetter(uint oid)
        {
            GHContainerWindowPage page;
            if (_containerPages.TryGetValue(oid, out page) && page.Letter != '\0')
                return page.Letter;
            List<GHMenuItem> items = _inventoryItems;
            if (items != null)
            {
                foreach (GHMenuItem mi in items)
                {
                    if (mi != null && mi.HasObjData && mi.ObjData.ObjData.o_id == oid)
                        return (char)mi.Identifier;
                }
            }
            return '\0';
        }

        /*
         * Overseer window
         */

        /* Opens the page in its own window; returns false if it should be shown as a modal page instead */
        public static bool OpenOverseerWindow(Page page)
        {
            if (!IsEnabled || page == null)
                return false;
            if (_overseerWindow != null)
            {
                Activate(_overseerWindow);
                return true;
            }
            Window window = new Window(page) { Title = "Gnoll Overseer" };
            RestoreGeometry(window, "Overseer", 900, 800);
            window.Destroying += OverseerWindow_Destroying;
            _overseerWindow = window;
            Application.Current?.OpenWindow(window);
            return true;
        }

        public static bool IsOverseerWindowOpen { get { return _overseerWindow != null; } }

        private static void OverseerWindow_Destroying(object sender, EventArgs e)
        {
            Window window = sender as Window;
            if (window == null)
                return;
            window.Destroying -= OverseerWindow_Destroying;
            SaveGeometry(window, "Overseer");
            if (window == _overseerWindow)
                _overseerWindow = null;
        }

        public static void CloseWindowOf(Page page)
        {
            Window window = page?.Window;
            if (window != null)
                Application.Current?.CloseWindow(window);
        }

        /* Closes every separate window, e.g. when the main window closes; the inventory window opens again next time */
        public static void CloseAll()
        {
            if (!IsSupported)
                return;
            _closingAll = true;
            try
            {
                List<Window> windows = new List<Window>(_containerWindows.Values);
                if (_inventoryWindow != null)
                    windows.Add(_inventoryWindow);
                if (_overseerWindow != null)
                    windows.Add(_overseerWindow);
                _containerWindows.Clear();
                _containerPages.Clear();
                foreach (Window window in windows)
                    Application.Current?.CloseWindow(window);
            }
            finally
            {
                _closingAll = false;
            }
        }

        /* Called on the UI thread when the game has ended and the main page is back */
        public static void GameEnded()
        {
            if (!IsSupported)
                return;
            CloseAll();
            _game = null;
            _configGame = null;
            _inventoryItems = null;
            _pendingActions.Clear();
        }

        /* Called after the separate windows setting has changed */
        public static void SettingsChanged()
        {
            if (!IsSupported)
                return;
            if (!GHApp.SeparateGuiWindows)
                CloseAll();
            _configGame = null; /* Sent again when the game next waits for a command */
            if (_game != null && _game.AwaitingCommand)
                NotifyAwaitingCommand(_game, true);
        }

        /*
         * Item command popup
         */

        public static void SetPendingPopup(GHGridWindowPage page, SKPoint point, string title)
        {
            _popupPage = page;
            _popupPoint = point;
            _popupTitle = title;
            _popupTime = DateTime.Now;
        }

        /* Shows the item command menu in the window that asked for it; returns false if the game page should show it */
        public static bool TryShowItemCommandPopup(GHGame game, GHMenuInfo menuInfo, GHPublishedWindow window)
        {
            if (!IsSupported || menuInfo == null || menuInfo.Style != ghmenu_styles.GHMENU_STYLE_ITEM_COMMAND)
                return false;
            GHGridWindowPage page = _popupPage;
            _popupPage = null;
            if (page == null || menuInfo.SelectionHow != SelectionMode.Single
                || (DateTime.Now - _popupTime).TotalMilliseconds > PopupPendingMilliseconds || page.Window == null)
                return false;
            _popupGame = game;
            _popupWindow = window;
            _popupShownPage = page;
            page.View.ShowPopup(menuInfo.MenuItems, _popupPoint, _popupTitle);
            page.Invalidate();
            Activate(page.Window);
            return true;
        }

        public static void RespondPopup(bool cancelled, GHMenuItem selected)
        {
            GHGame game = _popupGame;
            GHPublishedWindow window = _popupWindow;
            GHGridWindowPage page = _popupShownPage;
            _popupGame = null;
            _popupWindow = null;
            _popupShownPage = null;
            if (game == null)
                return;
            if (!cancelled && selected != null && page != null)
            {
                /* If the core shows the item's command menu again, e.g. after Examine, it comes back to this window */
                _popupPage = page;
                _popupTime = DateTime.Now;
            }
            List<GHMenuItem> list = new List<GHMenuItem>(1);
            if (!cancelled && selected != null)
            {
                selected.Count = -1;
                list.Add(selected);
            }
            GHWindow origWindow = null;
            if (window?.PublishedFrom != null)
                window.PublishedFrom.TryGetTarget(out origWindow);
            game.ResponseQueue.Enqueue(new GHResponse(game, GHRequestType.ShowMenuPage, origWindow, list, cancelled || list.Count == 0));
        }

        /*
         * Dragging between windows
         */

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const int GWLP_HWNDPARENT = -8;

        /* The window stays above the main window, but not above other programs, and minimizes with it */
        private static void OwnedWindow_Created(object sender, EventArgs e)
        {
            try
            {
                Window window = sender as Window;
                window.Created -= OwnedWindow_Created;
                Microsoft.UI.Xaml.Window nativeWindow = window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                if (nativeWindow == null || GHApp.WindowsXamlWindow == null)
                    return;
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                IntPtr owner = WinRT.Interop.WindowNative.GetWindowHandle(GHApp.WindowsXamlWindow);
                if (hwnd != IntPtr.Zero && owner != IntPtr.Zero && hwnd != owner)
                    SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, owner);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /* The view's bounds on the screen in pixels */
        private static bool TryGetScreenRect(View view, out SKRect rect)
        {
            rect = new SKRect();
            Microsoft.UI.Xaml.FrameworkElement element = view?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
            Microsoft.UI.Xaml.Window nativeWindow = view?.Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (element == null || nativeWindow == null || element.XamlRoot == null)
                return false;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            Windows.Foundation.Point origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = element.XamlRoot.RasterizationScale;
            POINT client = new POINT();
            if (!ClientToScreen(hwnd, ref client))
                return false;
            float left = (float)(client.X + origin.X * scale);
            float top = (float)(client.Y + origin.Y * scale);
            rect = new SKRect(left, top, left + (float)(element.ActualWidth * scale), top + (float)(element.ActualHeight * scale));
            return true;
        }

        private static bool IsCursorOver(View view)
        {
            POINT p;
            SKRect rect;
            return view != null && GetCursorPos(out p) && TryGetScreenRect(view, out rect) && rect.Contains(p.X, p.Y);
        }

        public static bool FindContainerWindowUnderCursor(out uint oid)
        {
            oid = 0;
            foreach (KeyValuePair<uint, GHContainerWindowPage> kv in _containerPages)
            {
                if (IsCursorOver(kv.Value.Canvas))
                {
                    oid = kv.Key;
                    return true;
                }
            }
            return false;
        }

        public static bool IsCursorOverWindowOf(GHGridWindowPage page)
        {
            return page != null && IsCursorOver(page.Canvas);
        }
#else
        public static void NotifyAwaitingCommand(GHGame game, bool awaiting) { }
        public static void PublishMenu(GHGame game, GHPublishedWindow window) { }
        public static void OpenContainerWindow(GHGame game, uint oid) { }
        public static void CloseContainerWindow(uint oid) { }
        public static void OpenInventoryWindow(GHGame game) { }
        public static bool TryShowItemCommandPopup(GHGame game, GHMenuInfo menuInfo, GHPublishedWindow window) { return false; }
        public static bool FindContainerWindowUnderCursor(out uint oid) { oid = 0; return false; }
        public static char GetContainerLetter(uint oid) { return '\0'; }
        public static void CloseAll() { }
        public static void GameEnded() { }
        public static void SettingsChanged() { }
#endif
    }

#if GNH_MAUI && WINDOWS && !GNH_AVALONIA
    /* A separate window drawing a GHGridInventoryView on its own canvas */
    public abstract class GHGridWindowPage : ContentPage, IGHGridInventoryHost
    {
        private static readonly SKColor BackgroundSkColor = new SKColor(0x14, 0x10, 0x0C);

        public SKCanvasView Canvas { get; }
        public GHGridInventoryView View { get; }

        protected GHGridWindowPage(GHGridViewMode mode)
        {
            BackgroundColor = Color.FromRgb(0x14, 0x10, 0x0C);
            Canvas = new SKCanvasView { EnableTouchEvents = true };
            Canvas.PaintSurface += Canvas_PaintSurface;
            Canvas.Touch += Canvas_Touch;
            Content = Canvas;
            View = new GHGridInventoryView(this, mode, GamePage.EquipmentSlotList);
        }

        public void Invalidate()
        {
            Canvas.InvalidateSurface();
        }

        private void Canvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            canvas.Clear(BackgroundSkColor);
            View.Disabled = GHGuiWindows.ShowAsBusy;
            View.Paint(canvas, e.Info.Width, e.Info.Height);
        }

        private void Canvas_Touch(object sender, SKTouchEventArgs e)
        {
            View.HandleTouch(e);
            e.Handled = true;
            Canvas.InvalidateSurface();
        }

        public bool UseGL { get { return false; } }
        public bool HighFilterQuality { get { return true; } }
        public bool IsTwoWeap { get { return false; } }

        public void InvalidateGrid()
        {
            Canvas.InvalidateSurface();
        }

        public abstract void OnGridAction(GHGridInventoryView view, int menuIdx, int action, int target, SKPoint point);
        public abstract bool OnDragReleasedOutside(GHGridInventoryView view, int menuIdx, GHGridHitKind sourceKind);

        public void OnPopupRowSelected(GHGridInventoryView view, int menuIdx)
        {
            IList<GHMenuItem> items = view.PopupItems;
            GHMenuItem mi = items != null && menuIdx >= 0 && menuIdx < items.Count ? items[menuIdx] : null;
            view.ClosePopup();
            GHGuiWindows.RespondPopup(mi == null, mi);
            Invalidate();
        }

        public void OnPopupCancelled(GHGridInventoryView view)
        {
            view.ClosePopup();
            GHGuiWindows.RespondPopup(true, null);
            Invalidate();
        }
    }

    public sealed class GHInventoryWindowPage : GHGridWindowPage
    {
        public GHInventoryWindowPage() : base(GHGridViewMode.Inventory)
        {
            Title = "Inventory";
        }

        public void UpdateItems(List<GHMenuItem> items, string header, string subtitle)
        {
            View.Items = items;
            if (Window != null)
                Window.Title = "GnollHack - " + (string.IsNullOrEmpty(header) ? "Inventory" : header) + (string.IsNullOrEmpty(subtitle) ? "" : " (" + subtitle + ")");
            Invalidate();
        }

        public override void OnGridAction(GHGridInventoryView view, int menuIdx, int action, int target, SKPoint point)
        {
            GHMenuItem mi = view.GetMenuItem(menuIdx);
            if (mi == null)
                return;
            if (action == GHConstants.INVENTORY_ACTION_MENU || action == GHConstants.INVENTORY_ACTION_DEFAULT)
                GHGuiWindows.SetPendingPopup(this, point, mi.TrimmedMainText);
            GHGuiWindows.SendAction(action, (char)mi.Identifier, target, 0);
        }

        public override bool OnDragReleasedOutside(GHGridInventoryView view, int menuIdx, GHGridHitKind sourceKind)
        {
            GHMenuItem mi = view.GetMenuItem(menuIdx);
            uint oid;
            if (mi == null || sourceKind != GHGridHitKind.Cell || !GHGuiWindows.FindContainerWindowUnderCursor(out oid))
                return false;
            /* Onto a container window: put in */
            char containerLetter = GHGuiWindows.GetContainerLetter(oid);
            if (containerLetter == '\0' || containerLetter == (char)mi.Identifier)
                return false;
            return GHGuiWindows.SendAction(GHConstants.INVENTORY_ACTION_STASH, (char)mi.Identifier, containerLetter, 0);
        }
    }

    public sealed class GHContainerWindowPage : GHGridWindowPage
    {
        public uint ContainerOid { get; }
        public char Letter { get; private set; }

        public GHContainerWindowPage(uint containerOid) : base(GHGridViewMode.Container)
        {
            ContainerOid = containerOid;
            Title = "Container";
        }

        public void UpdateContents(List<GHMenuItem> items, string header, string subtitle, char letter)
        {
            Letter = letter;
            View.Items = items;
            View.Title = (string.IsNullOrEmpty(header) ? "Container" : header) + (string.IsNullOrEmpty(subtitle) ? "" : " - " + subtitle);
            if (Window != null)
                Window.Title = "GnollHack - " + (string.IsNullOrEmpty(header) ? "Container" : header);
            Invalidate();
        }

        private void TakeOut(GHGridInventoryView view, int menuIdx)
        {
            GHMenuItem mi = view.GetMenuItem(menuIdx);
            if (mi == null || Letter == '\0')
                return;
            GHGuiWindows.SendAction(GHConstants.INVENTORY_ACTION_TAKEOUT, Letter, 0, unchecked((uint)mi.Identifier));
        }

        public override void OnGridAction(GHGridInventoryView view, int menuIdx, int action, int target, SKPoint point)
        {
            if (action == GHConstants.INVENTORY_ACTION_TAKEOUT)
                TakeOut(view, menuIdx);
        }

        public override bool OnDragReleasedOutside(GHGridInventoryView view, int menuIdx, GHGridHitKind sourceKind)
        {
            /* Anywhere outside this window, such as the inventory window: take out */
            TakeOut(view, menuIdx);
            return true;
        }
    }
#endif
}
