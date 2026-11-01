using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SkiaSharp;

#if GNH_MAUI
using GnollHackM;
using SkiaSharp.Views.Maui;
#else
using SkiaSharp.Views.Forms;
#endif

namespace GnollHackX
{
    public enum GHGridViewMode
    {
        Inventory = 0, /* equipment dock, 52 letter slots and the detail panel */
        Container,     /* the contents of a container in order, and the detail panel */
    }

    public enum GHGridHitKind
    {
        None = 0,
        Cell,
        Dock,
        Detail,
        PopupRow,
        Popup,
    }

    public struct GHGridHit
    {
        public GHGridHitKind Kind;
        public int Index;
        public GHGridHit(GHGridHitKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }
    }

    /* What the owner of a grid view does with the player's actions */
    public interface IGHGridInventoryHost
    {
        bool UseGL { get; }
        bool HighFilterQuality { get; }
        bool IsTwoWeap { get; }
        void InvalidateGrid();
        /* An action on the item at menuIdx: one of GHConstants.INVENTORY_ACTION_*, with its target; point is where it
           happened, for the item command popup */
        void OnGridAction(GHGridInventoryView view, int menuIdx, int action, int target, SKPoint point);
        void OnPopupRowSelected(GHGridInventoryView view, int menuIdx);
        void OnPopupCancelled(GHGridInventoryView view);
        /* A drag released outside the view's canvas; returns true if another window took it */
        bool OnDragReleasedOutside(GHGridInventoryView view, int menuIdx, GHGridHitKind sourceKind);
    }

    /* The grid inventory: draws inventory or container menu rows as slots with the item tiles, with a detail panel for
       the selected item and an item command popup, and turns clicks, double clicks, right clicks, long taps and drags
       into actions for its host. Paint and touch both run on the UI thread. */
    public sealed class GHGridInventoryView
    {
        public const int InventoryColumns = 8;
        public const int InventoryCells = 54; /* a-z, A-Z, $ and # */
        public const int ContainerColumns = 6;

        public static readonly char[] InventoryLetters = CreateInventoryLetters();

        private static char[] CreateInventoryLetters()
        {
            char[] letters = new char[InventoryCells];
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

        private readonly IGHGridInventoryHost _host;
        private readonly EquipmentSlot[] _equipmentSlots;

        public GHGridViewMode Mode { get; }

        /* The rows of the inventory or container menu */
        public IList<GHMenuItem> Items { get; set; }
        /* The rows of the item command menu shown as a popup over the items, or null */
        public IList<GHMenuItem> PopupItems { get; private set; }
        public SKPoint PopupPoint { get; private set; }
        public string PopupTitle { get; private set; }
        /* Inputs are ignored and the view is dimmed, e.g. while the game waits for an answer elsewhere */
        public bool Disabled { get; set; }
        /* Shown above a container's items */
        public string Title { get; set; }
        /* Drawn in the top right corner when set; a click on it calls PopOutClicked */
        public bool ShowPopOutButton { get; set; }
        public event EventHandler PopOutClicked;
        /* Selection, by inventory letter in inventory mode and by o_id in container mode */
        public long SelectedIdentifier { get; set; }

        private SKPoint _hoverPoint;
        private bool _isHovering;
        private long _lastClickIdentifier;
        private DateTime _lastClickTime = DateTime.MinValue;
        private long _pressId = -1;
        private SKPoint _pressPoint;
        private DateTime _pressTime;
        private SKMouseButton _pressButton = SKMouseButton.Unknown;
        private GHGridHit _pressHit;
        private bool _pressOnPopOut;
        private bool _dragging;
        private SKPoint _dragPoint;

        /* Layout, written by paint and read by touch */
        private float _canvasWidth = 1;
        private float _canvasHeight = 1;
        private SKRect[] _cellRects = new SKRect[0];
        private SKRect[] _dockRects = new SKRect[0];
        private SKRect _detailRect = new SKRect();
        private SKRect _popOutRect = new SKRect();
        private SKRect _popupRect = new SKRect();
        private readonly List<SKRect> _popupRowRects = new List<SKRect>();
        private readonly List<int> _popupRowItemIndices = new List<int>();

        private readonly GHSkiaFontPaint _textPaint = new GHSkiaFontPaint(1, 1, 1);
        private readonly SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        private readonly SKPaint _imagePaint = new SKPaint();

        private static readonly SKColor PanelColor = new SKColor(0x20, 0x18, 0x10, 0xC8);
        private static readonly SKColor PanelLightColor = new SKColor(0xF4, 0xE8, 0xD0, 0xE0);
        private static readonly SKColor SelectedFillColor = new SKColor(0xFF, 0x99, 0x22, 0x70);
        private static readonly SKColor HoverFillColor = new SKColor(0xFF, 0xFF, 0xFF, 0x30);
        private static readonly SKColor DropValidColor = new SKColor(0x40, 0xD0, 0x40, 0x60);
        private static readonly SKColor DropTargetColor = new SKColor(0x60, 0xFF, 0x60, 0xA0);
        private static readonly SKColor WornColor = new SKColor(0xFF, 0xCC, 0x44, 0xE0);
        private static readonly SKColor DimColor = new SKColor(0x00, 0x00, 0x00, 0x90);
        private static readonly SKColor LetterColor = new SKColor(0xD8, 0xC8, 0xA8);
        private static readonly SKColor LetterLightColor = new SKColor(0x6B, 0x45, 0x20);
        private static readonly Regex SymbolRegex = new Regex("&([A-Za-z0-9]+);");

        public GHGridInventoryView(IGHGridInventoryHost host, GHGridViewMode mode, EquipmentSlot[] equipmentSlots)
        {
            _host = host;
            Mode = mode;
            _equipmentSlots = equipmentSlots ?? new EquipmentSlot[0];
        }

        public bool IsDragging { get { return _dragging; } }
        public bool IsPopupShown { get { return PopupItems != null; } }

        public void ShowPopup(IList<GHMenuItem> popupItems, SKPoint point, string title)
        {
            PopupItems = popupItems;
            PopupPoint = point;
            PopupTitle = CleanText(title);
            ResetPress();
        }

        public void ClosePopup()
        {
            PopupItems = null;
            ResetPress();
        }

        public void ResetPress()
        {
            _pressId = -1;
            _dragging = false;
            _pressOnPopOut = false;
        }

        public void SetHover(SKPoint point, bool isHovering)
        {
            _hoverPoint = point;
            _isHovering = isHovering;
        }

        /* Replaces special symbol tokens such as &AC; with their plain names, as the view draws no symbol icons */
        public static string CleanText(string text)
        {
            return string.IsNullOrEmpty(text) ? text : SymbolRegex.Replace(text, "$1");
        }

        public static string GetSlotName(EquipmentSlot[] slots, long wornBits)
        {
            if (wornBits == 0 || slots == null)
                return "";
            foreach (EquipmentSlot slot in slots)
            {
                if (wornBits == (long)slot.WornFlag)
                    return slot.SlotName;
            }
            return "";
        }

        public static int GetSlotTarget(obj_worn_flags wornFlag)
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
        public static string GetDefaultActionName(GHMenuItem mi, GHGridViewMode mode)
        {
            if (mi == null || !mi.HasObjData)
                return "";
            if (mode == GHGridViewMode.Container)
                return "Take out";
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
         * Items
         */

        public int FindLetterIndex(char letter)
        {
            IList<GHMenuItem> items = Items;
            if (items == null || letter == '\0')
                return -1;
            for (int i = 0; i < items.Count; i++)
            {
                GHMenuItem mi = items[i];
                if (mi != null && mi.Identifier == (long)letter && mi.HasObjData)
                    return i;
            }
            return -1;
        }

        public int FindWornIndex(obj_worn_flags wornFlag)
        {
            IList<GHMenuItem> items = Items;
            if (items == null)
                return -1;
            for (int i = 0; i < items.Count; i++)
            {
                GHMenuItem mi = items[i];
                if (mi != null && mi.Identifier != 0 && mi.ObjWornBits == (long)wornFlag)
                    return i;
            }
            return -1;
        }

        /* The menu index of the n-th item row of a container */
        private int FindContainerItemIndex(int n)
        {
            IList<GHMenuItem> items = Items;
            if (items == null || n < 0)
                return -1;
            int k = 0;
            for (int i = 0; i < items.Count; i++)
            {
                GHMenuItem mi = items[i];
                if (mi != null && mi.Identifier != 0 && mi.HasObjData)
                {
                    if (k == n)
                        return i;
                    k++;
                }
            }
            return -1;
        }

        private int CountContainerItems()
        {
            IList<GHMenuItem> items = Items;
            int k = 0;
            if (items != null)
            {
                foreach (GHMenuItem mi in items)
                {
                    if (mi != null && mi.Identifier != 0 && mi.HasObjData)
                        k++;
                }
            }
            return k;
        }

        public int GetHitMenuIndex(GHGridHit hit)
        {
            if (hit.Kind == GHGridHitKind.Cell)
            {
                if (Mode == GHGridViewMode.Container)
                    return FindContainerItemIndex(hit.Index);
                if (hit.Index >= 0 && hit.Index < InventoryCells)
                    return FindLetterIndex(InventoryLetters[hit.Index]);
            }
            if (hit.Kind == GHGridHitKind.Dock && hit.Index >= 0 && hit.Index < _equipmentSlots.Length)
                return FindWornIndex(_equipmentSlots[hit.Index].WornFlag);
            return -1;
        }

        public GHMenuItem GetMenuItem(int menuIdx)
        {
            IList<GHMenuItem> items = Items;
            return items != null && menuIdx >= 0 && menuIdx < items.Count ? items[menuIdx] : null;
        }

        private GHMenuItem GetSelectedItem()
        {
            IList<GHMenuItem> items = Items;
            if (items == null || SelectedIdentifier == 0)
                return null;
            foreach (GHMenuItem mi in items)
            {
                if (mi != null && mi.Identifier == SelectedIdentifier && mi.HasObjData)
                    return mi;
            }
            return null;
        }

        /*
         * Painting
         */

        private void DrawSlotBitmap(SKCanvas canvas, SKImage bitmap, SKRect rect, SKColorFilter filter)
        {
            if (bitmap == null)
                return;
            _imagePaint.ColorFilter = filter;
            canvas.DrawImage(bitmap, rect
#if GNH_MAUI
                , SKSamplingOptions.Default
#endif
                , _imagePaint);
            _imagePaint.ColorFilter = null;
        }

        private void DrawItemGlyph(SKCanvas canvas, GHMenuItem mi, SKRect rect, bool isHover, float opacity)
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
                    bool hq = _host.HighFilterQuality;
                    float s = Math.Min(rect.Width / gw, rect.Height / gh);
                    canvas.Translate(rect.Left + (rect.Width - gw * s) / 2, rect.Top + (rect.Height - gh * s) / 2);
                    canvas.Scale(s);
                    mi.GlyphImageSource.DrawOnCanvas(canvas, _host.UseGL, isHover, hq, GHApp.FixRects, hq && GHApp.FixFiltering, opacity);
                }
            }
            finally
            {
                canvas.Restore();
            }
        }

        private void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, SKTextAlign align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            _textPaint.Typeface = GHApp.UnderwoodTypeface;
            _textPaint.TextSize = size;
            _textPaint.Color = color;
            _textPaint.Style = SKPaintStyle.Fill;
            _textPaint.DrawTextOnCanvas(canvas, text, x, y, align);
        }

        private float MeasureText(string text, float size)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            _textPaint.Typeface = GHApp.UnderwoodTypeface;
            _textPaint.TextSize = size;
            return _textPaint.MeasureText(text);
        }

        /* Word-wraps text to lines no wider than width */
        private List<string> WrapText(string text, float size, float width)
        {
            List<string> lines = new List<string>();
            text = CleanText(text);
            if (string.IsNullOrEmpty(text))
                return lines;
            string[] words = text.Split(' ');
            string line = "";
            foreach (string word in words)
            {
                if (word.Length == 0)
                    continue;
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && MeasureText(candidate, size) > width)
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

        public void Paint(SKCanvas canvas, float canvaswidth, float canvasheight)
        {
            _textPaint.SyncBlobCache(false);
            _canvasWidth = Math.Max(1, canvaswidth);
            _canvasHeight = Math.Max(1, canvasheight);
            IList<GHMenuItem> items = Items;
            if (items == null)
                items = new List<GHMenuItem>();

            if (PopupItems != null)
            {
                PaintItems(canvas, canvaswidth, canvasheight, items, false);
                _fillPaint.Color = DimColor;
                canvas.DrawRect(new SKRect(0, 0, canvaswidth, canvasheight), _fillPaint);
                PaintPopup(canvas, canvaswidth, canvasheight, PopupItems);
            }
            else
            {
                PaintItems(canvas, canvaswidth, canvasheight, items, true);
                if (Disabled)
                {
                    _fillPaint.Color = new SKColor(0, 0, 0, 0x60);
                    canvas.DrawRect(new SKRect(0, 0, canvaswidth, canvasheight), _fillPaint);
                }
            }
        }

        /* Draws the dock, the slots and the detail panel; interactive is false for the popup's backdrop */
        private void PaintItems(SKCanvas canvas, float canvaswidth, float canvasheight, IList<GHMenuItem> menuItems, bool interactive)
        {
            float scale = GHApp.DisplayDensity * GHApp.CustomScreenScale;
            float margin = 8 * scale;
            bool isDarkMode = GHApp.DarkMode;
            bool isLandscape = canvaswidth > canvasheight * 1.1f;
            bool isContainer = Mode == GHGridViewMode.Container;
            int numSlots = isContainer ? 0 : _equipmentSlots.Length;
            int columns = isContainer ? ContainerColumns : InventoryColumns;
            int numCells = isContainer ? Math.Max(columns, ((CountContainerItems() + columns - 1) / columns) * columns) : InventoryCells;
            int dockColumns = numSlots == 0 ? 0 : isLandscape ? 4 : InventoryColumns;
            int dockRows = numSlots == 0 ? 0 : (numSlots + dockColumns - 1) / dockColumns;
            int gridRows = (numCells + columns - 1) / columns;
            float titleHeight = isContainer && !string.IsNullOrEmpty(Title) ? 0.5f : 0;
            float availw = Math.Max(1, canvaswidth - 2 * margin);
            float availh = Math.Max(1, canvasheight - 2 * margin);
            float detailUnits = isLandscape ? 4.5f : 0;
            float dockUnits = dockColumns > 0 && isLandscape ? dockColumns + 0.5f : 0;
            float cell;
            if (isLandscape)
                cell = Math.Min(availw / (dockUnits + columns + 0.5f + detailUnits), availh / (Math.Max(dockRows, gridRows) + titleHeight));
            else
                cell = Math.Min(availw / columns, availh / (dockRows + (dockRows > 0 ? 0.4f : 0) + titleHeight + gridRows + 0.4f + 2.6f));
            cell = Math.Min(cell, 110 * scale);
            float pad = cell * 0.04f;
            float totalw = isLandscape ? cell * (dockUnits + columns + 0.5f + detailUnits) : cell * columns;
            float left = Math.Max(margin, (canvaswidth - totalw) / 2);
            float top = margin;

            SKRect[] dockRects = new SKRect[numSlots];
            SKRect[] cellRects = new SKRect[numCells];
            float gridLeft, gridTop, dockLeft = left, dockTop = top;
            SKRect detailRect;
            if (isLandscape)
            {
                gridLeft = left + cell * dockUnits;
                gridTop = top + cell * titleHeight;
                float detailLeft = gridLeft + cell * (columns + 0.5f);
                detailRect = new SKRect(detailLeft, top, detailLeft + cell * 4.5f, Math.Min(canvasheight - margin, top + cell * Math.Max(Math.Max(dockRows, gridRows) + titleHeight, 4)));
            }
            else
            {
                gridLeft = left;
                gridTop = top + cell * (dockRows + (dockRows > 0 ? 0.4f : 0) + titleHeight);
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
            for (i = 0; i < numCells; i++)
            {
                float x = gridLeft + (i % columns) * cell;
                float y = gridTop + (i / columns) * cell;
                cellRects[i] = new SKRect(x + pad, y + pad, x + cell - pad, y + cell - pad);
            }
            float popOutSize = Math.Max(32 * scale, cell * 0.5f);
            SKRect popOutRect = new SKRect();
            if (ShowPopOutButton)
            {
                /* Above the detail panel, which moves down to make room */
                popOutRect = new SKRect(detailRect.Right - popOutSize, detailRect.Top, detailRect.Right, detailRect.Top + popOutSize);
                detailRect = new SKRect(detailRect.Left, detailRect.Top + popOutSize + pad * 2, detailRect.Right, Math.Max(detailRect.Top + popOutSize + pad * 2 + cell, detailRect.Bottom));
            }

            if (interactive)
            {
                _cellRects = cellRects;
                _dockRects = dockRects;
                _detailRect = detailRect;
                _popOutRect = popOutRect;
            }

            SKPoint hoverPoint = _hoverPoint;
            bool isHovering = interactive && _isHovering && !Disabled;
            bool dragging = interactive && _dragging;
            GHMenuItem dragItem = null;
            if (dragging)
            {
                dragItem = GetMenuItem(GetHitMenuIndex(_pressHit));
                hoverPoint = _dragPoint;
                isHovering = true;
            }
            bool menuIsTwoWeap = _host.IsTwoWeap;

            SKImage slotBitmap = isDarkMode ? GHApp.InventorySlotDarkBitmap : GHApp.InventorySlotLightBitmap;
            SKColor letterColor = isDarkMode ? LetterColor : LetterLightColor;
            float frameInset = cell * 0.17f;
            GHMenuItem hoverItem = null;
            SKRect hoverRect = new SKRect();

            if (isContainer && !string.IsNullOrEmpty(Title))
                DrawText(canvas, CleanText(Title), gridLeft, top + cell * 0.36f, cell * 0.24f, WornColor, SKTextAlign.Left);

            /* Equipment dock */
            for (i = 0; i < numSlots; i++)
            {
                EquipmentSlot slot = _equipmentSlots[i];
                SKRect r = dockRects[i];
                int idx = FindWornIndex(slot.WornFlag);
                GHMenuItem mi = idx >= 0 ? menuItems[idx] : null;
                bool isHover = isHovering && r.Contains(hoverPoint);
                bool isSelected = mi != null && SelectedIdentifier != 0 && mi.Identifier == SelectedIdentifier;
                if (dragging && dragItem != null && _pressHit.Kind == GHGridHitKind.Cell && dragItem.EquipmentSlotMatch(slot, menuIsTwoWeap))
                {
                    _fillPaint.Color = isHover ? DropTargetColor : DropValidColor;
                    canvas.DrawRect(r, _fillPaint);
                }
                else if (isSelected)
                {
                    _fillPaint.Color = SelectedFillColor;
                    canvas.DrawRect(r, _fillPaint);
                }
                DrawSlotBitmap(canvas, slotBitmap, r, isHover ? UIUtils.HighlightColorFilter : null);
                SKRect inner = new SKRect(r.Left + frameInset, r.Top + frameInset, r.Right - frameInset, r.Bottom - frameInset);
                bool beingDragged = dragging && _pressHit.Kind == GHGridHitKind.Dock && _pressHit.Index == i;
                if (mi != null)
                {
                    DrawItemGlyph(canvas, mi, inner, isHover, beingDragged ? 0.35f : 1.0f);
                    if (mi.MaxCount > 1)
                        DrawText(canvas, mi.MaxCount.ToString(), inner.Right, r.Bottom - frameInset * 0.35f, cell * 0.2f, letterColor, SKTextAlign.Right);
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
                    DrawSlotBitmap(canvas, icon, inner, isDarkMode ? UIUtils.InventoryDarkUnwornColorFilter : UIUtils.InventoryLightUnwornColorFilter);
                }
            }

            /* Slots */
            for (i = 0; i < numCells; i++)
            {
                SKRect r = cellRects[i];
                int idx = GetHitMenuIndex(new GHGridHit(GHGridHitKind.Cell, i));
                GHMenuItem mi = idx >= 0 ? menuItems[idx] : null;
                bool isHover = isHovering && r.Contains(hoverPoint);
                bool isSelected = mi != null && SelectedIdentifier != 0 && mi.Identifier == SelectedIdentifier;
                bool beingDragged = dragging && _pressHit.Kind == GHGridHitKind.Cell && _pressHit.Index == i;
                if (dragging && isHover && !beingDragged && !isContainer)
                {
                    _fillPaint.Color = DropTargetColor;
                    canvas.DrawRect(r, _fillPaint);
                }
                else if (isSelected)
                {
                    _fillPaint.Color = SelectedFillColor;
                    canvas.DrawRect(r, _fillPaint);
                }
                else if (isHover && mi != null)
                {
                    _fillPaint.Color = HoverFillColor;
                    canvas.DrawRect(r, _fillPaint);
                }
                DrawSlotBitmap(canvas, slotBitmap, r, isHover ? UIUtils.HighlightColorFilter : null);
                if (!isContainer)
                {
                    char letter = InventoryLetters[i];
                    float letterSize = cell * 0.2f;
                    if (mi != null)
                    {
                        /* A dark backing keeps the letter readable over the tile */
                        _fillPaint.Color = new SKColor(0, 0, 0, 0x90);
                        canvas.DrawRoundRect(new SKRect(r.Left + frameInset * 0.25f, r.Top + frameInset * 0.25f, r.Left + frameInset * 0.25f + letterSize * 1.0f, r.Top + frameInset * 0.25f + letterSize * 1.1f), letterSize * 0.2f, letterSize * 0.2f, _fillPaint);
                    }
                    DrawText(canvas, letter.ToString(), r.Left + frameInset * 0.25f + letterSize * 0.5f, r.Top + frameInset * 0.25f + letterSize * 0.9f, letterSize, mi != null ? SKColors.White : letterColor.WithAlpha(0x70), SKTextAlign.Center);
                }
                if (mi == null)
                    continue;

                SKRect inner = new SKRect(r.Left + frameInset, r.Top + frameInset, r.Right - frameInset, r.Bottom - frameInset);
                DrawItemGlyph(canvas, mi, inner, isHover, beingDragged ? 0.35f : 1.0f);
                if (mi.MaxCount > 1)
                    DrawText(canvas, mi.MaxCount.ToString(), inner.Right, r.Bottom - frameInset * 0.35f, cell * 0.2f, letterColor, SKTextAlign.Right);
                if (mi.IsObjWorn)
                {
                    _strokePaint.Color = WornColor;
                    _strokePaint.StrokeWidth = Math.Max(1, cell * 0.03f);
                    float d = cell * 0.06f;
                    canvas.DrawRoundRect(new SKRect(r.Left + d, r.Top + d, r.Right - d, r.Bottom - d), d, d, _strokePaint);
                }
                if (isHover)
                {
                    hoverItem = mi;
                    hoverRect = r;
                }
            }

            /* Detail panel */
            GHMenuItem selItem = GetSelectedItem();
            PaintDetailPanel(canvas, detailRect, cell, selItem, dragging, dragging && detailRect.Contains(hoverPoint), isDarkMode);

            if (ShowPopOutButton && interactive)
            {
                bool popHover = isHovering && popOutRect.Contains(hoverPoint);
                _fillPaint.Color = popHover ? SelectedFillColor : PanelColor;
                canvas.DrawRoundRect(popOutRect, popOutSize * 0.2f, popOutSize * 0.2f, _fillPaint);
                _strokePaint.Color = WornColor;
                _strokePaint.StrokeWidth = Math.Max(1, scale * 1.5f);
                float q = popOutSize * 0.25f;
                canvas.DrawRect(new SKRect(popOutRect.Left + q, popOutRect.Top + q * 1.4f, popOutRect.Right - q * 1.4f, popOutRect.Bottom - q), _strokePaint);
                canvas.DrawLine(popOutRect.Right - q * 1.6f, popOutRect.Top + q, popOutRect.Right - q, popOutRect.Top + q, _strokePaint);
                canvas.DrawLine(popOutRect.Right - q, popOutRect.Top + q, popOutRect.Right - q, popOutRect.Top + q * 1.6f, _strokePaint);
                DrawText(canvas, "Open in own window", popOutRect.Left - popOutSize * 0.2f, popOutRect.MidY + popOutSize * 0.15f, Math.Max(10, popOutSize * 0.38f), popHover ? SKColors.White : letterColor, SKTextAlign.Right);
            }

            /* Dragged item at the pointer */
            if (dragging && dragItem != null)
            {
                SKRect dr = new SKRect(hoverPoint.X - cell * 0.4f, hoverPoint.Y - cell * 0.4f, hoverPoint.X + cell * 0.4f, hoverPoint.Y + cell * 0.4f);
                DrawItemGlyph(canvas, dragItem, dr, false, 0.85f);
            }
            else if (interactive && hoverItem != null && hoverItem != selItem)
            {
                PaintTooltip(canvas, canvaswidth, canvasheight, hoverItem, hoverRect, cell);
            }
        }

        private void PaintDetailPanel(SKCanvas canvas, SKRect rect, float cell, GHMenuItem mi, bool dragging, bool dropHover, bool isDarkMode)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;
            bool isContainer = Mode == GHGridViewMode.Container;
            float radius = cell * 0.12f;
            bool dropZone = dragging && !isContainer;
            _fillPaint.Color = dropZone ? (dropHover ? DropTargetColor : DropValidColor) : (isDarkMode ? PanelColor : PanelLightColor);
            canvas.DrawRoundRect(rect, radius, radius, _fillPaint);
            SKColor textColor = isDarkMode ? SKColors.White : SKColors.Black;
            SKColor dimTextColor = isDarkMode ? new SKColor(0xC0, 0xB8, 0xA8) : new SKColor(0x60, 0x50, 0x40);
            float textSize = Math.Max(10, cell * 0.22f);
            float x = rect.Left + cell * 0.2f;
            float width = rect.Width - cell * 0.4f;
            float y = rect.Top + cell * 0.2f;

            if (dragging)
            {
                string text = isContainer ? "Release over the inventory window to take the item out." : "Release here to drop the item.";
                foreach (string line in WrapText(text, textSize, width))
                {
                    y += textSize * 1.25f;
                    DrawText(canvas, line, x, y, textSize, textColor, SKTextAlign.Left);
                }
                return;
            }

            if (mi == null)
            {
                string[] hints = isContainer
                    ? new string[] { "Click an item to select it.", "Double-click: take out.", "Drag to the inventory window: take out." }
                    : new string[] { "Click an item to select it.", "Double-click: default action.", "Right-click: all actions.", "Drag: equip, move, stash or drop." };
                foreach (string hint in hints)
                {
                    foreach (string line in WrapText(hint, textSize, width))
                    {
                        y += textSize * 1.25f;
                        if (y > rect.Bottom)
                            return;
                        DrawText(canvas, line, x, y, textSize, dimTextColor, SKTextAlign.Left);
                    }
                }
                return;
            }

            bool isTallBox = rect.Height > rect.Width;
            float glyphSize = cell * (isTallBox ? 1.5f : 1.2f);
            SKRect glyphRect = new SKRect(x, y, x + glyphSize, y + glyphSize);
            DrawItemGlyph(canvas, mi, glyphRect, false, 1.0f);
            float tx = x;
            float twidth = width;
            if (isTallBox)
            {
                y += glyphSize;
            }
            else
            {
                tx = x + glyphSize + cell * 0.2f;
                twidth = width - glyphSize - cell * 0.2f;
            }

            List<string> lines = new List<string>();
            lines.AddRange(WrapText(mi.TrimmedMainText, textSize, twidth));
            int nameLines = lines.Count;
            if (mi.IsSuffixTextVisible)
                lines.AddRange(WrapText(mi.TrimmedSuffixText, textSize * 0.9f, twidth));
            if (mi.IsSuffix2TextVisible)
                lines.AddRange(WrapText(mi.TrimmedSuffix2Text, textSize * 0.9f, twidth));
            string slotName = GetSlotName(_equipmentSlots, mi.ObjWornBits);
            if (!string.IsNullOrEmpty(slotName))
                lines.Add("Equipped: " + slotName);
            int infoLines = lines.Count;
            lines.Add("Double-click: " + GetDefaultActionName(mi, Mode));
            if (!isContainer)
                lines.Add("Right-click: all actions");

            for (int i = 0; i < lines.Count; i++)
            {
                float size = i < nameLines ? textSize : textSize * 0.9f;
                y += size * 1.25f;
                if (i == infoLines)
                    y += size * 0.4f;
                if (y > rect.Bottom - cell * 0.1f)
                    break;
                DrawText(canvas, lines[i], tx, y, size, i < nameLines ? textColor : dimTextColor, SKTextAlign.Left);
            }
        }

        private void PaintTooltip(SKCanvas canvas, float canvaswidth, float canvasheight, GHMenuItem mi, SKRect anchor, float cell)
        {
            string text = mi.TrimmedMainText;
            if (string.IsNullOrEmpty(text))
                return;
            float size = Math.Max(10, cell * 0.2f);
            float maxw = Math.Min(canvaswidth * 0.6f, cell * 6);
            List<string> lines = WrapText(text, size, maxw);
            float w = 0;
            foreach (string line in lines)
                w = Math.Max(w, MeasureText(line, size));
            float padx = size * 0.5f;
            float h = lines.Count * size * 1.25f + size * 0.5f;
            float x = anchor.Left;
            float y = anchor.Bottom + size * 0.2f;
            if (x + w + 2 * padx > canvaswidth)
                x = Math.Max(0, canvaswidth - w - 2 * padx);
            if (y + h > canvasheight)
                y = Math.Max(0, anchor.Top - h - size * 0.2f);
            SKRect box = new SKRect(x, y, x + w + 2 * padx, y + h);
            _fillPaint.Color = new SKColor(0x10, 0x0C, 0x08, 0xE8);
            canvas.DrawRoundRect(box, size * 0.3f, size * 0.3f, _fillPaint);
            _strokePaint.Color = WornColor.WithAlpha(0x80);
            _strokePaint.StrokeWidth = 1;
            canvas.DrawRoundRect(box, size * 0.3f, size * 0.3f, _strokePaint);
            float ty = y;
            foreach (string line in lines)
            {
                ty += size * 1.25f;
                DrawText(canvas, line, x + padx, ty, size, SKColors.White, SKTextAlign.Left);
            }
        }

        /* Splits a command row such as "Wear   (W)" into the command and its shortcut; with tab separation the shortcut is the suffix */
        private static void SplitCommandText(GHMenuItem mi, out string command, out string shortcut)
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

        private void PaintPopup(SKCanvas canvas, float canvaswidth, float canvasheight, IList<GHMenuItem> menuItems)
        {
            float scale = GHApp.DisplayDensity * GHApp.CustomScreenScale;
            float size = 16 * scale;
            float rowh = size * 1.7f;
            float headh = size * 1.4f;
            float padx = size * 0.8f;
            float margin = 6 * scale;
            string title = PopupTitle;
            float textw = MeasureText(title, size);
            float height = (string.IsNullOrEmpty(title) ? 0 : rowh) + size * 0.4f;
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
                    textw = Math.Max(textw, MeasureText(mi.MainText.Trim(), size * 0.8f));
                }
                else
                {
                    string command, shortcut;
                    SplitCommandText(mi, out command, out shortcut);
                    height += rowh;
                    textw = Math.Max(textw, MeasureText(command, size) + (shortcut.Length > 0 ? MeasureText(shortcut, size * 0.85f) + size * 1.5f : 0));
                }
            }
            float width = Math.Min(canvaswidth - 2 * margin, textw + 2 * padx);
            height = Math.Min(canvasheight - 2 * margin, height);
            float x = PopupPoint.X;
            float y = PopupPoint.Y;
            if (x + width > canvaswidth - margin)
                x = Math.Max(margin, canvaswidth - margin - width);
            if (y + height > canvasheight - margin)
                y = Math.Max(margin, canvasheight - margin - height);
            SKRect box = new SKRect(x, y, x + width, y + height);

            _fillPaint.Color = new SKColor(0x1C, 0x14, 0x0C, 0xF4);
            canvas.DrawRoundRect(box, size * 0.4f, size * 0.4f, _fillPaint);
            _strokePaint.Color = WornColor;
            _strokePaint.StrokeWidth = Math.Max(1, scale * 1.5f);
            canvas.DrawRoundRect(box, size * 0.4f, size * 0.4f, _strokePaint);

            List<SKRect> rowRects = new List<SKRect>();
            List<int> rowIndices = new List<int>();
            float cy = y + size * 0.2f;
            canvas.Save();
            canvas.ClipRect(box);
            if (!string.IsNullOrEmpty(title))
            {
                DrawText(canvas, title, x + padx, cy + rowh * 0.68f, size, WornColor, SKTextAlign.Left);
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
                    _fillPaint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x30);
                    canvas.DrawRect(new SKRect(x + padx * 0.5f, cy + headh * 0.15f, x + width - padx * 0.5f, cy + headh * 0.15f + Math.Max(1, scale)), _fillPaint);
                    DrawText(canvas, mi.MainText.Trim(), x + padx, cy + headh * 0.8f, size * 0.8f, new SKColor(0xC0, 0xB0, 0x90), SKTextAlign.Left);
                    cy += headh;
                }
                else
                {
                    SKRect row = new SKRect(x, cy, x + width, cy + rowh);
                    if (_isHovering && row.Contains(_hoverPoint))
                    {
                        _fillPaint.Color = SelectedFillColor;
                        canvas.DrawRect(row, _fillPaint);
                    }
                    string command, shortcut;
                    SplitCommandText(mi, out command, out shortcut);
                    DrawText(canvas, command, x + padx, cy + rowh * 0.66f, size, SKColors.White, SKTextAlign.Left);
                    if (shortcut.Length > 0)
                        DrawText(canvas, shortcut, x + width - padx, cy + rowh * 0.66f, size * 0.85f, new SKColor(0xA0, 0xA0, 0xA0), SKTextAlign.Right);
                    rowRects.Add(row);
                    rowIndices.Add(i);
                    cy += rowh;
                }
            }
            canvas.Restore();

            _popupRect = box;
            _popupRowRects.Clear();
            _popupRowRects.AddRange(rowRects);
            _popupRowItemIndices.Clear();
            _popupRowItemIndices.AddRange(rowIndices);
        }

        /*
         * Input
         */

        public GHGridHit HitTest(SKPoint p)
        {
            if (PopupItems != null)
            {
                for (int j = 0; j < _popupRowRects.Count; j++)
                {
                    if (_popupRowRects[j].Contains(p))
                        return new GHGridHit(GHGridHitKind.PopupRow, _popupRowItemIndices[j]);
                }
                if (_popupRect.Contains(p))
                    return new GHGridHit(GHGridHitKind.Popup, -1);
                return new GHGridHit(GHGridHitKind.None, -1);
            }
            for (int i = 0; i < _cellRects.Length; i++)
            {
                if (_cellRects[i].Contains(p))
                    return new GHGridHit(GHGridHitKind.Cell, i);
            }
            for (int i = 0; i < _dockRects.Length; i++)
            {
                if (_dockRects[i].Contains(p))
                    return new GHGridHit(GHGridHitKind.Dock, i);
            }
            if (_detailRect.Contains(p))
                return new GHGridHit(GHGridHitKind.Detail, -1);
            return new GHGridHit(GHGridHitKind.None, -1);
        }

        private bool IsOutsideCanvas(SKPoint p)
        {
            return p.X < 0 || p.Y < 0 || p.X >= _canvasWidth || p.Y >= _canvasHeight;
        }

        public void HandleTouch(SKTouchEventArgs e)
        {
            if (e == null)
                return;
            switch (e.ActionType)
            {
                case SKTouchAction.Entered:
                    SetHover(e.Location, true);
                    break;
                case SKTouchAction.Exited:
                    if (!e.InContact)
                        SetHover(e.Location, false);
                    break;
                case SKTouchAction.Pressed:
                    SetHover(e.Location, true);
                    if (Disabled && PopupItems == null)
                    {
                        e.Handled = true;
                        break;
                    }
                    _pressId = e.Id;
                    _pressPoint = e.Location;
                    _pressTime = DateTime.Now;
                    _pressButton = e.MouseButton;
                    _pressHit = HitTest(e.Location);
                    _pressOnPopOut = ShowPopOutButton && PopupItems == null && _popOutRect.Contains(e.Location);
                    _dragging = false;
                    e.Handled = true;
                    break;
                case SKTouchAction.Moved:
                    SetHover(e.Location, true);
                    if (e.Id == _pressId && e.InContact)
                    {
                        float dx = e.Location.X - _pressPoint.X;
                        float dy = e.Location.Y - _pressPoint.Y;
                        if (!_dragging && PopupItems == null && !_pressOnPopOut && _pressButton != SKMouseButton.Right
                            && (dx * dx + dy * dy) > GHConstants.MoveDistanceThreshold * GHConstants.MoveDistanceThreshold
                            && GetHitMenuIndex(_pressHit) >= 0)
                        {
                            _dragging = true;
                        }
                        if (_dragging)
                            _dragPoint = e.Location;
                    }
                    _host.InvalidateGrid();
                    e.Handled = true;
                    break;
                case SKTouchAction.Released:
                    if (e.Id == _pressId)
                    {
                        _pressId = -1;
                        if (PopupItems != null)
                            HandlePopupRelease(e);
                        else if (_pressOnPopOut)
                        {
                            if (_popOutRect.Contains(e.Location))
                                PopOutClicked?.Invoke(this, EventArgs.Empty);
                        }
                        else if (_dragging)
                            HandleDrop(e.Location);
                        else
                            HandleClick(e);
                        _dragging = false;
                        _pressOnPopOut = false;
                        _host.InvalidateGrid();
                    }
                    e.Handled = true;
                    break;
                case SKTouchAction.Cancelled:
                    ResetPress();
                    _host.InvalidateGrid();
                    e.Handled = true;
                    break;
                default:
                    break;
            }
        }

        private void HandlePopupRelease(SKTouchEventArgs e)
        {
            GHGridHit hit = HitTest(e.Location);
            if (hit.Kind == GHGridHitKind.PopupRow && e.MouseButton != SKMouseButton.Right)
                _host.OnPopupRowSelected(this, hit.Index);
            else if (hit.Kind != GHGridHitKind.Popup)
                _host.OnPopupCancelled(this); /* Outside the popup or a right click */
        }

        private void HandleClick(SKTouchEventArgs e)
        {
            GHGridHit hit = HitTest(e.Location);
            int idx = GetHitMenuIndex(hit);
            GHMenuItem mi = GetMenuItem(idx);
            DateTime now = DateTime.Now;
            bool isRight = e.MouseButton == SKMouseButton.Right;
            bool isLongTap = !isRight && e.DeviceType != SKTouchDeviceType.Mouse
                && (now - _pressTime).TotalSeconds >= GHConstants.LongMenuTapThreshold;
            bool isContainer = Mode == GHGridViewMode.Container;

            if (mi == null)
            {
                SelectedIdentifier = 0;
                _lastClickIdentifier = 0;
                return;
            }

            if (isRight || isLongTap)
            {
                SelectedIdentifier = mi.Identifier;
                _lastClickIdentifier = 0;
                _host.OnGridAction(this, idx, isContainer ? GHConstants.INVENTORY_ACTION_TAKEOUT : GHConstants.INVENTORY_ACTION_MENU, 0, e.Location);
                return;
            }

            if (mi.Identifier == _lastClickIdentifier && (now - _lastClickTime).TotalMilliseconds <= GHConstants.DoubleClickTimeThreshold)
            {
                _lastClickIdentifier = 0;
                _host.OnGridAction(this, idx, isContainer ? GHConstants.INVENTORY_ACTION_TAKEOUT : GHConstants.INVENTORY_ACTION_DEFAULT, 0, e.Location);
                return;
            }

            SelectedIdentifier = mi.Identifier;
            _lastClickIdentifier = mi.Identifier;
            _lastClickTime = now;
        }

        private void HandleDrop(SKPoint p)
        {
            GHGridHit source = _pressHit;
            int idx = GetHitMenuIndex(source);
            GHMenuItem mi = GetMenuItem(idx);
            if (mi == null)
                return;

            if (IsOutsideCanvas(p))
            {
                _host.OnDragReleasedOutside(this, idx, source.Kind);
                return;
            }
            if (Mode == GHGridViewMode.Container)
                return; /* Items are taken out by dropping them on the inventory window */

            GHGridHit target = HitTest(p);
            char letter = (char)mi.Identifier;
            switch (target.Kind)
            {
                case GHGridHitKind.Cell:
                    if (source.Kind == GHGridHitKind.Dock)
                    {
                        _host.OnGridAction(this, idx, GHConstants.INVENTORY_ACTION_UNEQUIP, 0, p);
                    }
                    else
                    {
                        char targetLetter = InventoryLetters[target.Index];
                        if (targetLetter == letter)
                            return;
                        SelectedIdentifier = targetLetter; /* The selection follows the moved item */
                        _host.OnGridAction(this, idx, GHConstants.INVENTORY_ACTION_ADJUST, targetLetter, p);
                    }
                    break;
                case GHGridHitKind.Dock:
                    if (source.Kind == GHGridHitKind.Dock)
                        return;
                    {
                        EquipmentSlot slot = _equipmentSlots[target.Index];
                        if (mi.ObjWornBits == (long)slot.WornFlag)
                            return;
                        SelectedIdentifier = mi.Identifier;
                        _host.OnGridAction(this, idx, GHConstants.INVENTORY_ACTION_EQUIP, GetSlotTarget(slot.WornFlag), p);
                    }
                    break;
                case GHGridHitKind.Detail:
                case GHGridHitKind.None:
                    _host.OnGridAction(this, idx, GHConstants.INVENTORY_ACTION_DROP, 0, p);
                    break;
                default:
                    break;
            }
        }
    }
}
