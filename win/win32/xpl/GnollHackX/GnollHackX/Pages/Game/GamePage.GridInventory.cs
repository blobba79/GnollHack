using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
    /* Grid inventory: draws the inventory menu as 52 letter slots next to the equipment slots, with a detail panel for
       the selected item. A click selects, a double click sends the default action, a right click (or a long tap) opens
       the item command menu as a popup at the pointer, and dragging sends an equip, unequip, adjust, stash or drop
       action. Actions reach the C core as the picked count of the selected item (GHConstants.InventoryActionCode).
       Paint and touch both run on the UI thread; the layout rectangles are still shared under _gridLayoutLock. */
    public partial class GamePage
    {
        private const int GridInventoryColumns = 8;
        private const int GridInventoryCells = 54; /* a-z, A-Z, $ and # */
        private const long GridContextPendingMilliseconds = 3000;

        private static readonly char[] _gridInventoryLetters = CreateGridInventoryLetters();

        private static char[] CreateGridInventoryLetters()
        {
            char[] letters = new char[GridInventoryCells];
            int i;
            for (i = 0; i < 26; i++)
            {
                letters[i] = (char)('a' + i);
                letters[26 + i] = (char)('A' + i);
            }
            letters[52] = '$';
            letters[53] = '#';
            return letters;
        }

        private enum GridHitKind
        {
            None = 0,
            Cell,
            Dock,
            Detail,
            PopupRow,
            Popup,
        }

        private struct GridHit
        {
            public GridHitKind Kind;
            public int Index;
            public GridHit(GridHitKind kind, int index)
            {
                Kind = kind;
                Index = index;
            }
        }

        private int _gridInventoryShown = 0;
        private int _gridContextPopupShown = 0;
        private bool GridInventoryShown { get { return Interlocked.CompareExchange(ref _gridInventoryShown, 0, 0) != 0; } set { Interlocked.Exchange(ref _gridInventoryShown, value ? 1 : 0); } }
        private bool GridContextPopupShown { get { return Interlocked.CompareExchange(ref _gridContextPopupShown, 0, 0) != 0; } set { Interlocked.Exchange(ref _gridContextPopupShown, value ? 1 : 0); } }

        /* True if the menu canvas is drawn and handled by the grid inventory; a gamepad uses the list */
        private bool GridInventoryPaintActive { get { return (GridInventoryShown || GridContextPopupShown) && !GHGamepadService.IsActive; } }

        /* UI thread state */
        private char _gridSelectedLetter = '\0';
        private char _gridLastClickLetter = '\0';
        private DateTime _gridLastClickTime = DateTime.MinValue;
        private long _gridPressId = -1;
        private SKPoint _gridPressPoint;
        private DateTime _gridPressTime;
        private SKMouseButton _gridPressButton = SKMouseButton.Unknown;
        private GridHit _gridPressHit;
        private bool _gridDragging = false;
        private SKPoint _gridDragPoint;
        private bool _gridContextPending = false;
        private SKPoint _gridContextPoint;
        private DateTime _gridContextPendingTime = DateTime.MinValue;
        private ObservableCollection<GHMenuItem> _gridBackdropItems = null;
        private string _gridBackdropTitle = null;

        /* Layout, written by paint and read by touch */
        private readonly object _gridLayoutLock = new object();
        private readonly SKRect[] _gridCellRects = new SKRect[GridInventoryCells];
        private SKRect[] _gridDockRects = null;
        private SKRect _gridDetailRect = new SKRect();
        private SKRect _gridPopupRect = new SKRect();
        private readonly List<SKRect> _gridPopupRowRects = new List<SKRect>();
        private readonly List<int> _gridPopupRowItemIndices = new List<int>();

        private readonly SKPaint _gridFillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _gridStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        private readonly SKPaint _gridImagePaint = new SKPaint();

        private readonly SKColor _gridPanelColor = new SKColor(0x20, 0x18, 0x10, 0xC8);
        private readonly SKColor _gridPanelLightColor = new SKColor(0xF4, 0xE8, 0xD0, 0xE0);
        private readonly SKColor _gridSelectedFillColor = new SKColor(0xFF, 0x99, 0x22, 0x70);
        private readonly SKColor _gridHoverFillColor = new SKColor(0xFF, 0xFF, 0xFF, 0x30);
        private readonly SKColor _gridDropValidColor = new SKColor(0x40, 0xD0, 0x40, 0x60);
        private readonly SKColor _gridDropTargetColor = new SKColor(0x60, 0xFF, 0x60, 0xA0);
        private readonly SKColor _gridWornColor = new SKColor(0xFF, 0xCC, 0x44, 0xE0);
        private readonly SKColor _gridDimColor = new SKColor(0x00, 0x00, 0x00, 0x90);
        private readonly SKColor _gridLetterColor = new SKColor(0xD8, 0xC8, 0xA8);
        private readonly SKColor _gridLetterLightColor = new SKColor(0x6B, 0x45, 0x20);

        /* Called when a menu is shown: decides whether it is drawn as the grid or as the command popup */
        private void UpdateGridInventoryModeForMenu(ghmenu_styles style, SelectionMode how)
        {
            bool grid = GHApp.InventoryGridMode && how == SelectionMode.Single
                && (style == ghmenu_styles.GHMENU_STYLE_INVENTORY || style == ghmenu_styles.GHMENU_STYLE_INVENTORY_EQUIPMENT);
            bool pendingValid = _gridContextPending
                && (DateTime.Now - _gridContextPendingTime).TotalMilliseconds <= GridContextPendingMilliseconds;
            bool popup = !grid && pendingValid && style == ghmenu_styles.GHMENU_STYLE_ITEM_COMMAND && how == SelectionMode.Single;

            _gridContextPending = false;
            if (!popup)
            {
                _gridBackdropItems = null;
                _gridBackdropTitle = null;
            }
            _gridPressId = -1;
            _gridDragging = false;

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

        private static int FindGridMenuItemIndex(IList<GHMenuItem> menuItems, char letter)
        {
            if (menuItems == null || letter == '\0')
                return -1;
            for (int i = 0; i < menuItems.Count; i++)
            {
                GHMenuItem mi = menuItems[i];
                if (mi != null && mi.Identifier == (long)letter && mi.HasObjData)
                    return i;
            }
            return -1;
        }

        private static int FindGridWornMenuItemIndex(IList<GHMenuItem> menuItems, obj_worn_flags wornFlag)
        {
            if (menuItems == null)
                return -1;
            for (int i = 0; i < menuItems.Count; i++)
            {
                GHMenuItem mi = menuItems[i];
                if (mi != null && mi.Identifier != 0 && mi.ObjWornBits == (long)wornFlag)
                    return i;
            }
            return -1;
        }

        private static int GetGridSlotTarget(obj_worn_flags wornFlag)
        {
            switch (wornFlag)
            {
                case obj_worn_flags.W_WEP:
                    return GHConstants.INVENTORY_SLOT_RIGHT_HAND;
                case obj_worn_flags.W_WEP2:
                    return GHConstants.INVENTORY_SLOT_LEFT_HAND;
                case obj_worn_flags.W_SWAPWEP:
                case obj_worn_flags.W_SWAPWEP2:
                    return GHConstants.INVENTORY_SLOT_SWAP;
                case obj_worn_flags.W_QUIVER:
                    return GHConstants.INVENTORY_SLOT_QUIVER;
                default:
                    if (((ulong)wornFlag & (ulong)obj_worn_flags.W_ACCESSORY) != 0)
                        return GHConstants.INVENTORY_SLOT_ACCESSORY;
                    return GHConstants.INVENTORY_SLOT_ARMOR;
            }
        }

        /* The name of what a double click does; the C core makes the actual choice */
        private static string GetGridDefaultActionName(GHMenuItem mi)
        {
            if (mi == null || !mi.HasObjData)
                return "";
            long worn = mi.ObjWornBits;
            if (worn != 0)
            {
                if ((worn & (long)obj_worn_flags.W_ARMOR) != 0)
                    return "Take off";
                if ((worn & (long)obj_worn_flags.W_ACCESSORY) != 0)
                    return "Remove";
                return "Unwield";
            }
            switch ((obj_class_types)mi.ObjClassType)
            {
                case obj_class_types.ARMOR_CLASS:
                    return "Wear";
                case obj_class_types.WEAPON_CLASS:
                    return "Wield";
                case obj_class_types.RING_CLASS:
                case obj_class_types.AMULET_CLASS:
                case obj_class_types.MISCELLANEOUS_CLASS:
                    return "Put on";
                case obj_class_types.POTION_CLASS:
                    return "Drink";
                case obj_class_types.SCROLL_CLASS:
                case obj_class_types.SPBOOK_CLASS:
                    return "Read";
                case obj_class_types.FOOD_CLASS:
                    return "Eat";
                case obj_class_types.WAND_CLASS:
                    return "Zap";
                case obj_class_types.TOOL_CLASS:
                    return "Use";
                default:
                    return "Show actions";
            }
        }

        /*
         * Painting
         */

        private void PaintGridInventory(SKCanvas canvas, float canvaswidth, float canvasheight, ObservableCollection<GHMenuItem> menuItems)
        {
            if (GridContextPopupShown)
            {
                if (_gridBackdropItems != null)
                    PaintGridInventoryView(canvas, canvaswidth, canvasheight, _gridBackdropItems, false);
                _gridFillPaint.Color = _gridDimColor;
                canvas.DrawRect(new SKRect(0, 0, canvaswidth, canvasheight), _gridFillPaint);
                PaintGridContextPopup(canvas, canvaswidth, canvasheight, menuItems);
            }
            else
            {
                PaintGridInventoryView(canvas, canvaswidth, canvasheight, menuItems, true);
            }
        }

        private void GetGridHover(out SKPoint hoverPoint, out bool isHovering)
        {
            lock (_menuHoverLock)
            {
                hoverPoint = _menuHoverPoint;
                isHovering = _menuIsHovering;
            }
        }

        private void DrawGridSlotBitmap(SKCanvas canvas, SKImage bitmap, SKRect rect, SKColorFilter filter)
        {
            if (bitmap == null)
                return;
            _gridImagePaint.ColorFilter = filter;
            canvas.DrawImage(bitmap, rect
#if GNH_MAUI
                , SKSamplingOptions.Default
#endif
                , _gridImagePaint);
            _gridImagePaint.ColorFilter = null;
        }

        private void DrawGridItemGlyph(SKCanvas canvas, GHMenuItem mi, SKRect rect, bool isHover, float opacity)
        {
            if (mi == null || mi.GlyphImageSource == null)
                return;
            canvas.Save();
            try
            {
                mi.GlyphImageSource.AutoSize = true;
                mi.GlyphImageSource.DoAutoSize();
                float gw = mi.GlyphImageSource.Width;
                float gh = mi.GlyphImageSource.Height;
                if (gw > 0 && gh > 0)
                {
                    float s = Math.Min(rect.Width / gw, rect.Height / gh);
                    canvas.Translate(rect.Left + (rect.Width - gw * s) / 2, rect.Top + (rect.Height - gh * s) / 2);
                    canvas.Scale(s);
                    mi.GlyphImageSource.DrawOnCanvas(canvas, MenuCanvas.UseGL, isHover, MenuHighFilterQuality, GHApp.FixRects,
                        MenuHighFilterQuality && GHApp.FixFiltering, opacity);
                }
            }
            finally
            {
                canvas.Restore();
            }
        }

        private void DrawGridText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, SKTextAlign align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            GHSkiaFontPaint textPaint = _menuTextPaint;
            textPaint.Typeface = GHApp.UnderwoodTypeface;
            textPaint.TextSize = size;
            textPaint.Color = color;
            textPaint.Style = SKPaintStyle.Fill;
            textPaint.DrawTextOnCanvas(canvas, text, x, y, align);
        }

        private float MeasureGridText(string text, float size)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            GHSkiaFontPaint textPaint = _menuTextPaint;
            textPaint.Typeface = GHApp.UnderwoodTypeface;
            textPaint.TextSize = size;
            return textPaint.MeasureText(text);
        }

        private static readonly Regex _gridSymbolRegex = new Regex("&([A-Za-z0-9]+);");

        /* Replaces special symbol tokens such as &AC; with their plain names, as this view draws no symbol icons */
        private static string CleanGridText(string text)
        {
            return string.IsNullOrEmpty(text) ? text : _gridSymbolRegex.Replace(text, "$1");
        }

        /* Word-wraps text to lines no wider than width */
        private List<string> WrapGridText(string text, float size, float width)
        {
            List<string> lines = new List<string>();
            text = CleanGridText(text);
            if (string.IsNullOrEmpty(text))
                return lines;
            string[] words = text.Split(' ');
            string line = "";
            foreach (string word in words)
            {
                if (word.Length == 0)
                    continue;
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && MeasureGridText(candidate, size) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            if (line.Length > 0)
                lines.Add(line);
            return lines;
        }

        /* Draws the dock, the letter grid and the detail panel; interactive is false for the popup's backdrop */
        private void PaintGridInventoryView(SKCanvas canvas, float canvaswidth, float canvasheight, IList<GHMenuItem> menuItems, bool interactive)
        {
            float scale = GHApp.DisplayDensity * GHApp.CustomScreenScale;
            float margin = 8 * scale;
            bool isDarkMode = GHApp.DarkMode;
            bool isLandscape = canvaswidth > canvasheight * 1.1f;
            int numSlots = _equipmentSlots.Length;
            int dockColumns = isLandscape ? 4 : GridInventoryColumns;
            int dockRows = (numSlots + dockColumns - 1) / dockColumns;
            int gridRows = (GridInventoryCells + GridInventoryColumns - 1) / GridInventoryColumns;
            float availw = Math.Max(1, canvaswidth - 2 * margin);
            float availh = Math.Max(1, canvasheight - 2 * margin);
            float cell;
            if (isLandscape)
                cell = Math.Min(availw / (dockColumns + 0.5f + GridInventoryColumns + 0.5f + 4.5f), availh / Math.Max(dockRows, gridRows));
            else
                cell = Math.Min(availw / GridInventoryColumns, availh / (dockRows + 0.4f + gridRows + 0.4f + 2.6f));
            cell = Math.Min(cell, 110 * scale);
            float pad = cell * 0.04f;
            float totalw = isLandscape ? cell * (dockColumns + 0.5f + GridInventoryColumns + 0.5f + 4.5f) : cell * GridInventoryColumns;
            float left = Math.Max(margin, (canvaswidth - totalw) / 2);
            float top = margin;

            SKRect[] dockRects = new SKRect[numSlots];
            SKRect[] cellRects = new SKRect[GridInventoryCells];
            float gridLeft, gridTop, dockLeft, dockTop;
            SKRect detailRect;
            if (isLandscape)
            {
                dockLeft = left;
                dockTop = top;
                gridLeft = left + cell * (dockColumns + 0.5f);
                gridTop = top;
                float detailLeft = gridLeft + cell * (GridInventoryColumns + 0.5f);
                detailRect = new SKRect(detailLeft, top, detailLeft + cell * 4.5f, top + cell * Math.Max(dockRows, gridRows));
            }
            else
            {
                dockLeft = left;
                dockTop = top;
                gridLeft = left;
                gridTop = top + cell * (dockRows + 0.4f);
                float detailTop = gridTop + cell * (gridRows + 0.4f);
                detailRect = new SKRect(left, detailTop, left + totalw, Math.Min(canvasheight - margin, detailTop + cell * 2.6f));
            }
            int i;
            for (i = 0; i < numSlots; i++)
            {
                float x = dockLeft + (i % dockColumns) * cell;
                float y = dockTop + (i / dockColumns) * cell;
                dockRects[i] = new SKRect(x + pad, y + pad, x + cell - pad, y + cell - pad);
            }
            for (i = 0; i < GridInventoryCells; i++)
            {
                float x = gridLeft + (i % GridInventoryColumns) * cell;
                float y = gridTop + (i / GridInventoryColumns) * cell;
                cellRects[i] = new SKRect(x + pad, y + pad, x + cell - pad, y + cell - pad);
            }

            if (interactive)
            {
                lock (_gridLayoutLock)
                {
                    cellRects.CopyTo(_gridCellRects, 0);
                    _gridDockRects = dockRects;
                    _gridDetailRect = detailRect;
                }
            }

            SKPoint hoverPoint = new SKPoint();
            bool isHovering = false;
            if (interactive)
                GetGridHover(out hoverPoint, out isHovering);
            bool dragging = interactive && _gridDragging;
            GHMenuItem dragItem = null;
            if (dragging)
            {
                dragItem = GetGridHitMenuItem(menuItems, _gridPressHit);
                hoverPoint = _gridDragPoint;
                isHovering = true;
            }
            bool menuIsTwoWeap = MenuIsTwoWeap;

            SKImage slotBitmap = isDarkMode ? GHApp.InventorySlotDarkBitmap : GHApp.InventorySlotLightBitmap;
            SKColor letterColor = isDarkMode ? _gridLetterColor : _gridLetterLightColor;
            float frameInset = cell * 0.17f;
            GHMenuItem hoverItem = null;
            SKRect hoverRect = new SKRect();

            /* Equipment dock */
            for (i = 0; i < numSlots; i++)
            {
                EquipmentSlot slot = _equipmentSlots[i];
                SKRect r = dockRects[i];
                int idx = FindGridWornMenuItemIndex(menuItems, slot.WornFlag);
                GHMenuItem mi = idx >= 0 ? menuItems[idx] : null;
                bool isHover = isHovering && r.Contains(hoverPoint);
                bool isSelected = mi != null && _gridSelectedLetter != '\0' && mi.Identifier == (long)_gridSelectedLetter;
                if (dragging && dragItem != null && _gridPressHit.Kind == GridHitKind.Cell && dragItem.EquipmentSlotMatch(slot, menuIsTwoWeap))
                {
                    _gridFillPaint.Color = isHover ? _gridDropTargetColor : _gridDropValidColor;
                    canvas.DrawRect(r, _gridFillPaint);
                }
                else if (isSelected)
                {
                    _gridFillPaint.Color = _gridSelectedFillColor;
                    canvas.DrawRect(r, _gridFillPaint);
                }
                DrawGridSlotBitmap(canvas, slotBitmap, r, isHover ? UIUtils.HighlightColorFilter : null);
                SKRect inner = new SKRect(r.Left + frameInset, r.Top + frameInset, r.Right - frameInset, r.Bottom - frameInset);
                bool beingDragged = dragging && _gridPressHit.Kind == GridHitKind.Dock && _gridPressHit.Index == i;
                if (mi != null)
                {
                    DrawGridItemGlyph(canvas, mi, inner, isHover, beingDragged ? 0.35f : 1.0f);
                    if (mi.MaxCount > 1)
                        DrawGridText(canvas, mi.MaxCount.ToString(), inner.Right, r.Bottom - frameInset * 0.35f, cell * 0.2f, letterColor, SKTextAlign.Right);
                    if (isHover)
                    {
                        hoverItem = mi;
                        hoverRect = r;
                    }
                }
                else
                {
                    int picIndex = slot.AltPictureStyle == 1 && !menuIsTwoWeap ? slot.AltPictureIndex : slot.PictureIndex;
                    SKImage icon = GHApp.InventoryIconBitmaps != null && picIndex >= 0 && picIndex < GHApp.InventoryIconBitmaps.Length ? GHApp.InventoryIconBitmaps[picIndex] : null;
                    DrawGridSlotBitmap(canvas, icon, inner, isDarkMode ? UIUtils.InventoryDarkUnwornColorFilter : UIUtils.InventoryLightUnwornColorFilter);
                }
            }

            /* Letter grid */
            for (i = 0; i < GridInventoryCells; i++)
            {
                char letter = _gridInventoryLetters[i];
                SKRect r = cellRects[i];
                int idx = FindGridMenuItemIndex(menuItems, letter);
                GHMenuItem mi = idx >= 0 ? menuItems[idx] : null;
                bool isHover = isHovering && r.Contains(hoverPoint);
                bool isSelected = mi != null && letter == _gridSelectedLetter;
                bool beingDragged = dragging && _gridPressHit.Kind == GridHitKind.Cell && _gridPressHit.Index == i;
                if (dragging && isHover && !beingDragged)
                {
                    _gridFillPaint.Color = _gridDropTargetColor;
                    canvas.DrawRect(r, _gridFillPaint);
                }
                else if (isSelected)
                {
                    _gridFillPaint.Color = _gridSelectedFillColor;
                    canvas.DrawRect(r, _gridFillPaint);
                }
                else if (isHover && mi != null)
                {
                    _gridFillPaint.Color = _gridHoverFillColor;
                    canvas.DrawRect(r, _gridFillPaint);
                }
                DrawGridSlotBitmap(canvas, slotBitmap, r, isHover ? UIUtils.HighlightColorFilter : null);
                DrawGridText(canvas, letter.ToString(), r.Left + frameInset * 0.45f, r.Top + frameInset * 0.45f + cell * 0.17f, cell * 0.17f, letterColor.WithAlpha(mi != null ? (byte)0xFF : (byte)0x70), SKTextAlign.Left);
                if (mi == null)
                    continue;

                SKRect inner = new SKRect(r.Left + frameInset, r.Top + frameInset, r.Right - frameInset, r.Bottom - frameInset);
                DrawGridItemGlyph(canvas, mi, inner, isHover, beingDragged ? 0.35f : 1.0f);
                if (mi.MaxCount > 1)
                    DrawGridText(canvas, mi.MaxCount.ToString(), inner.Right, r.Bottom - frameInset * 0.35f, cell * 0.2f, letterColor, SKTextAlign.Right);
                if (mi.IsObjWorn)
                {
                    _gridStrokePaint.Color = _gridWornColor;
                    _gridStrokePaint.StrokeWidth = Math.Max(1, cell * 0.03f);
                    float d = cell * 0.06f;
                    canvas.DrawRoundRect(new SKRect(r.Left + d, r.Top + d, r.Right - d, r.Bottom - d), d, d, _gridStrokePaint);
                }
                if (isHover)
                {
                    hoverItem = mi;
                    hoverRect = r;
                }
            }

            /* Detail panel */
            int selIdx = FindGridMenuItemIndex(menuItems, _gridSelectedLetter);
            GHMenuItem selItem = selIdx >= 0 ? menuItems[selIdx] : null;
            PaintGridDetailPanel(canvas, detailRect, cell, selItem, dragging, dragging && detailRect.Contains(hoverPoint), isDarkMode);

            /* Dragged item at the pointer */
            if (dragging && dragItem != null)
            {
                SKRect dr = new SKRect(hoverPoint.X - cell * 0.4f, hoverPoint.Y - cell * 0.4f, hoverPoint.X + cell * 0.4f, hoverPoint.Y + cell * 0.4f);
                DrawGridItemGlyph(canvas, dragItem, dr, false, 0.85f);
            }
            else if (interactive && hoverItem != null && hoverItem != selItem)
            {
                PaintGridTooltip(canvas, canvaswidth, canvasheight, hoverItem, hoverRect, cell);
            }
        }

        private void PaintGridDetailPanel(SKCanvas canvas, SKRect rect, float cell, GHMenuItem mi, bool dragging, bool dropHover, bool isDarkMode)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;
            float radius = cell * 0.12f;
            _gridFillPaint.Color = dragging ? (dropHover ? _gridDropTargetColor : _gridDropValidColor) : (isDarkMode ? _gridPanelColor : _gridPanelLightColor);
            canvas.DrawRoundRect(rect, radius, radius, _gridFillPaint);
            SKColor textColor = isDarkMode ? SKColors.White : SKColors.Black;
            SKColor dimTextColor = isDarkMode ? new SKColor(0xC0, 0xB8, 0xA8) : new SKColor(0x60, 0x50, 0x40);
            float textSize = Math.Max(10, cell * 0.22f);
            float x = rect.Left + cell * 0.2f;
            float width = rect.Width - cell * 0.4f;
            float y = rect.Top + cell * 0.2f;

            if (dragging)
            {
                foreach (string line in WrapGridText("Release here to drop the item.", textSize, width))
                {
                    y += textSize * 1.25f;
                    DrawGridText(canvas, line, x, y, textSize, textColor, SKTextAlign.Left);
                }
                return;
            }

            if (mi == null)
            {
                string[] hints = new string[] { "Click an item to select it.", "Double-click: default action.", "Right-click: all actions.", "Drag: equip, move, stash or drop." };
                foreach (string hint in hints)
                {
                    foreach (string line in WrapGridText(hint, textSize, width))
                    {
                        y += textSize * 1.25f;
                        if (y > rect.Bottom)
                            return;
                        DrawGridText(canvas, line, x, y, textSize, dimTextColor, SKTextAlign.Left);
                    }
                }
                return;
            }

            bool isLandscapeBox = rect.Height > rect.Width;
            float glyphSize = cell * (isLandscapeBox ? 1.5f : 1.2f);
            SKRect glyphRect = new SKRect(x, y, x + glyphSize, y + glyphSize);
            DrawGridItemGlyph(canvas, mi, glyphRect, false, 1.0f);
            float tx = x;
            float twidth = width;
            if (isLandscapeBox)
            {
                y += glyphSize;
            }
            else
            {
                tx = x + glyphSize + cell * 0.2f;
                twidth = width - glyphSize - cell * 0.2f;
            }

            List<string> lines = new List<string>();
            lines.AddRange(WrapGridText(mi.TrimmedMainText, textSize, twidth));
            int nameLines = lines.Count;
            if (mi.IsSuffixTextVisible)
                lines.AddRange(WrapGridText(mi.TrimmedSuffixText, textSize * 0.9f, twidth));
            if (mi.IsSuffix2TextVisible)
                lines.AddRange(WrapGridText(mi.TrimmedSuffix2Text, textSize * 0.9f, twidth));
            string slotName = GetInventorySlotName(mi.ObjWornBits);
            if (!string.IsNullOrEmpty(slotName))
                lines.Add("Equipped: " + slotName);
            int infoLines = lines.Count;
            lines.Add("Double-click: " + GetGridDefaultActionName(mi));
            lines.Add("Right-click: all actions");

            for (int i = 0; i < lines.Count; i++)
            {
                float size = i < nameLines ? textSize : textSize * 0.9f;
                y += size * 1.25f;
                if (i == infoLines)
                    y += size * 0.4f;
                if (y > rect.Bottom - cell * 0.1f)
                    break;
                DrawGridText(canvas, lines[i], tx, y, size, i < nameLines ? textColor : dimTextColor, SKTextAlign.Left);
            }
        }

        private void PaintGridTooltip(SKCanvas canvas, float canvaswidth, float canvasheight, GHMenuItem mi, SKRect anchor, float cell)
        {
            string text = mi.TrimmedMainText;
            if (string.IsNullOrEmpty(text))
                return;
            float size = Math.Max(10, cell * 0.2f);
            float maxw = Math.Min(canvaswidth * 0.6f, cell * 6);
            List<string> lines = WrapGridText(text, size, maxw);
            float w = 0;
            foreach (string line in lines)
                w = Math.Max(w, MeasureGridText(line, size));
            float padx = size * 0.5f;
            float h = lines.Count * size * 1.25f + size * 0.5f;
            float x = anchor.Left;
            float y = anchor.Bottom + size * 0.2f;
            if (x + w + 2 * padx > canvaswidth)
                x = Math.Max(0, canvaswidth - w - 2 * padx);
            if (y + h > canvasheight)
                y = Math.Max(0, anchor.Top - h - size * 0.2f);
            SKRect box = new SKRect(x, y, x + w + 2 * padx, y + h);
            _gridFillPaint.Color = new SKColor(0x10, 0x0C, 0x08, 0xE8);
            canvas.DrawRoundRect(box, size * 0.3f, size * 0.3f, _gridFillPaint);
            _gridStrokePaint.Color = _gridWornColor.WithAlpha(0x80);
            _gridStrokePaint.StrokeWidth = 1;
            canvas.DrawRoundRect(box, size * 0.3f, size * 0.3f, _gridStrokePaint);
            float ty = y;
            foreach (string line in lines)
            {
                ty += size * 1.25f;
                DrawGridText(canvas, line, x + padx, ty, size, SKColors.White, SKTextAlign.Left);
            }
        }

        /* Splits a command row such as "Wear   (W)" into the command and its shortcut; with tab separation the shortcut is the suffix */
        private static void SplitGridCommandText(GHMenuItem mi, out string command, out string shortcut)
        {
            command = mi.MainText == null ? "" : mi.MainText.Trim();
            shortcut = "";
            if (mi.IsSuffixTextVisible && command.IndexOf('(') < 0)
            {
                shortcut = mi.TrimmedSuffixText.Trim().Trim('(', ')');
                return;
            }
            int open = command.LastIndexOf('(');
            if (open > 0 && command.EndsWith(")"))
            {
                shortcut = command.Substring(open + 1, command.Length - open - 2);
                command = command.Substring(0, open).Trim();
            }
        }

        private void PaintGridContextPopup(SKCanvas canvas, float canvaswidth, float canvasheight, ObservableCollection<GHMenuItem> menuItems)
        {
            float scale = GHApp.DisplayDensity * GHApp.CustomScreenScale;
            float size = 16 * scale;
            float rowh = size * 1.7f;
            float headh = size * 1.4f;
            float padx = size * 0.8f;
            float margin = 6 * scale;
            float textw = MeasureGridText(_gridBackdropTitle, size);
            float height = (string.IsNullOrEmpty(_gridBackdropTitle) ? 0 : rowh) + size * 0.4f;
            int i;
            for (i = 0; i < menuItems.Count; i++)
            {
                GHMenuItem mi = menuItems[i];
                if (mi == null)
                    continue;
                if (mi.Identifier == 0)
                {
                    if (string.IsNullOrWhiteSpace(mi.MainText))
                        continue;
                    height += headh;
                    textw = Math.Max(textw, MeasureGridText(mi.MainText.Trim(), size * 0.8f));
                }
                else
                {
                    string command, shortcut;
                    SplitGridCommandText(mi, out command, out shortcut);
                    height += rowh;
                    textw = Math.Max(textw, MeasureGridText(command, size) + (shortcut.Length > 0 ? MeasureGridText(shortcut, size * 0.85f) + size * 1.5f : 0));
                }
            }
            float width = Math.Min(canvaswidth - 2 * margin, textw + 2 * padx);
            height = Math.Min(canvasheight - 2 * margin, height);
            float x = _gridContextPoint.X;
            float y = _gridContextPoint.Y;
            if (x + width > canvaswidth - margin)
                x = Math.Max(margin, canvaswidth - margin - width);
            if (y + height > canvasheight - margin)
                y = Math.Max(margin, canvasheight - margin - height);
            SKRect box = new SKRect(x, y, x + width, y + height);

            SKPoint hoverPoint;
            bool isHovering;
            GetGridHover(out hoverPoint, out isHovering);

            _gridFillPaint.Color = new SKColor(0x1C, 0x14, 0x0C, 0xF4);
            canvas.DrawRoundRect(box, size * 0.4f, size * 0.4f, _gridFillPaint);
            _gridStrokePaint.Color = _gridWornColor;
            _gridStrokePaint.StrokeWidth = Math.Max(1, scale * 1.5f);
            canvas.DrawRoundRect(box, size * 0.4f, size * 0.4f, _gridStrokePaint);

            List<SKRect> rowRects = new List<SKRect>();
            List<int> rowIndices = new List<int>();
            float cy = y + size * 0.2f;
            canvas.Save();
            canvas.ClipRect(box);
            if (!string.IsNullOrEmpty(_gridBackdropTitle))
            {
                DrawGridText(canvas, _gridBackdropTitle, x + padx, cy + rowh * 0.68f, size, _gridWornColor, SKTextAlign.Left);
                cy += rowh;
            }
            for (i = 0; i < menuItems.Count; i++)
            {
                GHMenuItem mi = menuItems[i];
                if (mi == null)
                    continue;
                if (mi.Identifier == 0)
                {
                    if (string.IsNullOrWhiteSpace(mi.MainText))
                        continue;
                    _gridFillPaint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x30);
                    canvas.DrawRect(new SKRect(x + padx * 0.5f, cy + headh * 0.15f, x + width - padx * 0.5f, cy + headh * 0.15f + Math.Max(1, scale)), _gridFillPaint);
                    DrawGridText(canvas, mi.MainText.Trim(), x + padx, cy + headh * 0.8f, size * 0.8f, new SKColor(0xC0, 0xB0, 0x90), SKTextAlign.Left);
                    cy += headh;
                }
                else
                {
                    SKRect row = new SKRect(x, cy, x + width, cy + rowh);
                    if (isHovering && row.Contains(hoverPoint))
                    {
                        _gridFillPaint.Color = _gridSelectedFillColor;
                        canvas.DrawRect(row, _gridFillPaint);
                    }
                    string command, shortcut;
                    SplitGridCommandText(mi, out command, out shortcut);
                    DrawGridText(canvas, command, x + padx, cy + rowh * 0.66f, size, SKColors.White, SKTextAlign.Left);
                    if (shortcut.Length > 0)
                        DrawGridText(canvas, shortcut, x + width - padx, cy + rowh * 0.66f, size * 0.85f, new SKColor(0xA0, 0xA0, 0xA0), SKTextAlign.Right);
                    rowRects.Add(row);
                    rowIndices.Add(i);
                    cy += rowh;
                }
            }
            canvas.Restore();

            lock (_gridLayoutLock)
            {
                _gridPopupRect = box;
                _gridPopupRowRects.Clear();
                _gridPopupRowRects.AddRange(rowRects);
                _gridPopupRowItemIndices.Clear();
                _gridPopupRowItemIndices.AddRange(rowIndices);
            }
        }

        /*
         * Input
         */

        private GridHit GridHitTest(SKPoint p)
        {
            lock (_gridLayoutLock)
            {
                if (GridContextPopupShown)
                {
                    for (int j = 0; j < _gridPopupRowRects.Count; j++)
                    {
                        if (_gridPopupRowRects[j].Contains(p))
                            return new GridHit(GridHitKind.PopupRow, _gridPopupRowItemIndices[j]);
                    }
                    if (_gridPopupRect.Contains(p))
                        return new GridHit(GridHitKind.Popup, -1);
                    return new GridHit(GridHitKind.None, -1);
                }
                for (int i = 0; i < GridInventoryCells; i++)
                {
                    if (_gridCellRects[i].Contains(p))
                        return new GridHit(GridHitKind.Cell, i);
                }
                if (_gridDockRects != null)
                {
                    for (int i = 0; i < _gridDockRects.Length; i++)
                    {
                        if (_gridDockRects[i].Contains(p))
                            return new GridHit(GridHitKind.Dock, i);
                    }
                }
                if (_gridDetailRect.Contains(p))
                    return new GridHit(GridHitKind.Detail, -1);
            }
            return new GridHit(GridHitKind.None, -1);
        }

        private int GetGridHitMenuItemIndex(IList<GHMenuItem> menuItems, GridHit hit)
        {
            if (hit.Kind == GridHitKind.Cell && hit.Index >= 0 && hit.Index < GridInventoryCells)
                return FindGridMenuItemIndex(menuItems, _gridInventoryLetters[hit.Index]);
            if (hit.Kind == GridHitKind.Dock && hit.Index >= 0 && hit.Index < _equipmentSlots.Length)
                return FindGridWornMenuItemIndex(menuItems, _equipmentSlots[hit.Index].WornFlag);
            return -1;
        }

        private GHMenuItem GetGridHitMenuItem(IList<GHMenuItem> menuItems, GridHit hit)
        {
            int idx = GetGridHitMenuItemIndex(menuItems, hit);
            return idx >= 0 ? menuItems[idx] : null;
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
            _gridPressId = -1;
            _gridDragging = false;
            MenuCanvas.InvalidateSurface();
            PressMenuOKButton();
        }

        /* The command popup is shown at p for the next item command menu */
        private void SetGridContextPending(SKPoint p, ObservableCollection<GHMenuItem> menuItems, GHMenuItem mi)
        {
            _gridContextPending = true;
            _gridContextPoint = p;
            _gridContextPendingTime = DateTime.Now;
            _gridBackdropItems = menuItems;
            _gridBackdropTitle = CleanGridText(mi?.TrimmedMainText);
        }

        private void HandleGridInventoryTouch(object sender, SKTouchEventArgs e)
        {
            if (e == null)
                return;
            switch (e.ActionType)
            {
                case SKTouchAction.Pressed:
                    _gridPressId = e.Id;
                    _gridPressPoint = e.Location;
                    _gridPressTime = DateTime.Now;
                    _gridPressButton = e.MouseButton;
                    _gridPressHit = GridHitTest(e.Location);
                    _gridDragging = false;
                    e.Handled = true;
                    break;
                case SKTouchAction.Moved:
                    if (e.Id == _gridPressId)
                    {
                        float dx = e.Location.X - _gridPressPoint.X;
                        float dy = e.Location.Y - _gridPressPoint.Y;
                        if (!_gridDragging && !GridContextPopupShown && _gridPressButton != SKMouseButton.Right
                            && (dx * dx + dy * dy) > GHConstants.MoveDistanceThreshold * GHConstants.MoveDistanceThreshold
                            && GetGridHitMenuItemIndex(MenuCanvas.MenuItems, _gridPressHit) >= 0)
                        {
                            _gridDragging = true;
                        }
                        if (_gridDragging)
                        {
                            _gridDragPoint = e.Location;
                            MenuCanvas.InvalidateSurface();
                        }
                    }
                    e.Handled = true;
                    break;
                case SKTouchAction.Released:
                    if (e.Id == _gridPressId)
                    {
                        _gridPressId = -1;
                        if (GridContextPopupShown)
                            HandleGridPopupRelease(e);
                        else if (_gridDragging)
                            HandleGridDrop(GridHitTest(e.Location));
                        else
                            HandleGridClick(e);
                        _gridDragging = false;
                        MenuCanvas.InvalidateSurface();
                    }
                    e.Handled = true;
                    break;
                case SKTouchAction.Cancelled:
                    _gridPressId = -1;
                    _gridDragging = false;
                    MenuCanvas.InvalidateSurface();
                    e.Handled = true;
                    break;
                default:
                    break;
            }
        }

        private void HandleGridPopupRelease(SKTouchEventArgs e)
        {
            GridHit hit = GridHitTest(e.Location);
            if (hit.Kind == GridHitKind.PopupRow && e.MouseButton != SKMouseButton.Right)
            {
                SendGridSelection(hit.Index, -1);
            }
            else if (hit.Kind != GridHitKind.Popup)
            {
                /* Outside the popup or a right click: back to the inventory */
                PressMenuCancelButton();
            }
        }

        private void HandleGridClick(SKTouchEventArgs e)
        {
            var menuItems = MenuCanvas.MenuItems;
            GridHit hit = GridHitTest(e.Location);
            int idx = GetGridHitMenuItemIndex(menuItems, hit);
            GHMenuItem mi = idx >= 0 ? menuItems[idx] : null;
            DateTime now = DateTime.Now;
            bool isRight = e.MouseButton == SKMouseButton.Right;
            bool isLongTap = !isRight && e.DeviceType != SKTouchDeviceType.Mouse
                && (now - _gridPressTime).TotalSeconds >= GHConstants.LongMenuTapThreshold;

            if (mi == null)
            {
                _gridSelectedLetter = '\0';
                _gridLastClickLetter = '\0';
                return;
            }

            char letter = (char)mi.Identifier;
            if (isRight || isLongTap)
            {
                _gridSelectedLetter = letter;
                _gridLastClickLetter = '\0';
                SetGridContextPending(e.Location, menuItems, mi);
                SendGridSelection(idx, -1);
                return;
            }

            if (letter == _gridLastClickLetter && (now - _gridLastClickTime).TotalMilliseconds <= GHConstants.DoubleClickTimeThreshold)
            {
                _gridLastClickLetter = '\0';
                /* If there is no default action, the item command menu comes up as the popup */
                SetGridContextPending(e.Location, menuItems, mi);
                SendGridSelection(idx, GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_DEFAULT, 0));
                return;
            }

            _gridSelectedLetter = letter;
            _gridLastClickLetter = letter;
            _gridLastClickTime = now;
        }

        private void HandleGridDrop(GridHit target)
        {
            var menuItems = MenuCanvas.MenuItems;
            GridHit source = _gridPressHit;
            int idx = GetGridHitMenuItemIndex(menuItems, source);
            if (idx < 0)
                return;
            GHMenuItem mi = menuItems[idx];
            char letter = (char)mi.Identifier;
            int code = 0;

            switch (target.Kind)
            {
                case GridHitKind.Cell:
                    if (source.Kind == GridHitKind.Dock)
                    {
                        code = GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_UNEQUIP, 0);
                    }
                    else
                    {
                        char targetLetter = _gridInventoryLetters[target.Index];
                        if (targetLetter == letter)
                            return;
                        code = GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_ADJUST, targetLetter);
                        _gridSelectedLetter = targetLetter; /* The selection follows the moved item */
                        SendGridSelection(idx, code);
                        return;
                    }
                    break;
                case GridHitKind.Dock:
                    if (source.Kind == GridHitKind.Dock)
                        return;
                    {
                        EquipmentSlot slot = _equipmentSlots[target.Index];
                        if (mi.ObjWornBits == (long)slot.WornFlag)
                            return;
                        code = GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_EQUIP, GetGridSlotTarget(slot.WornFlag));
                    }
                    break;
                case GridHitKind.Detail:
                case GridHitKind.None:
                    code = GHConstants.InventoryActionCode(GHConstants.INVENTORY_ACTION_DROP, 0);
                    break;
                default:
                    return;
            }

            _gridSelectedLetter = letter;
            SendGridSelection(idx, code);
        }
    }
}
