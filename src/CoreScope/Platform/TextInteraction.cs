using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoreScope.Platform;

/// <summary>
/// Makes the text in CoreScope selectable and copyable, everywhere, without restyling any TextBlock.
///  • Pressing the mouse on plain text lays a transparent read-only text box (same font, size and layout) over it and hands it
///    the click, so drag, double-click (word), triple-click (line), Ctrl+A and Ctrl+C all work. It goes away when you click elsewhere.
///  • Text inside buttons, tabs and list rows is left alone for the mouse (dragging there would fight the click), but every text
///    has a right-click menu: Copy (the whole text) and Copy everything on this page.
/// Registered once at startup; covers text created later by templates too.
/// </summary>
public static class TextInteraction
{
    private static readonly ContextMenu BlockMenu = BuildBlockMenu();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBoxBase, RangeSelection> Ranges = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // Class handlers on input events always fire (unlike Loaded, which WPF only raises for elements with their own handler).
        EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(OnRightMouseDown));
        EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown));
    }

    private static void OnRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock block) Prepare(block);
    }

    /// <summary>Gives the block the shared copy menu (unless it already has its own).</summary>
    public static void Prepare(TextBlock block)
    {
        if (IsInsideOverlay(block)) return; // the always-on-top overlay is dragged by its text
        block.ContextMenu ??= BlockMenu;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock block || e.Handled || e.ChangedButton != MouseButton.Left) return;
        if (BeginSelection(block, e) is not null) e.Handled = true;
    }

    // ───────────── Selection overlay ─────────────

    /// <summary>
    /// Starts selecting the text of <paramref name="block"/>. Returns the overlay text box, or null when the block is not eligible
    /// (click target, empty, hidden, no adorner layer). When <paramref name="mouse"/> is given, the press carries on as a drag selection.
    /// </summary>
    public static TextBoxBase? BeginSelection(TextBlock block, MouseButtonEventArgs? mouse = null)
    {
        if (!block.IsVisible || block.ActualWidth < 1 || block.ActualHeight < 1) return null;
        if (IsClickTarget(block) || IsInsideOverlay(block)) return null;
        var text = TextOf(block);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var layer = AdornerLayer.GetAdornerLayer(block);
        if (layer is null) return null;

        TextBoxBase box = IsMixed(block) ? CreateRichBox(block) : CreateBox(block, text);
        var adorner = new SelectionAdorner(block, box);
        var range = new RangeSelection(block, box);
        Ranges.Add(box, range);
        var previousOpacity = block.Opacity;
        block.Opacity = 0; // the overlay draws the same text; hide the original so it is not drawn twice
        layer.Add(adorner);
        box.LostKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is DependencyObject target && (target == box || IsDescendant(box, target))) return; // e.g. its own context menu
            range.Clear();
            Dismiss(layer, adorner, block, previousOpacity);
        };
        block.Unloaded += (_, _) => { range.Clear(); Dismiss(layer, adorner, block, previousOpacity); };

        layer.UpdateLayout();
        box.Focus();
        if (mouse is not null) BeginDrag(box, mouse.GetPosition(box));
        return box;
    }

    /// <summary>
    /// Carries on the press that created the overlay as a drag selection: the text under the press is where the selection starts, and
    /// the mouse is captured so it follows the pointer until the button is released. (Forwarding the click to the text box does not
    /// start a drag, because the box never saw the press itself.)
    /// </summary>
    private static void BeginDrag(TextBoxBase box, Point start)
    {
        var anchor = IndexAt(box, start);
        if (anchor < 0) return;
        Select(box, anchor, anchor);
        box.CaptureMouse();

        void Follow(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { Finish(); return; }
            if (Ranges.TryGetValue(box, out var range) && range.IsActive) return;
            var index = IndexAt(box, e.GetPosition(box));
            if (index >= 0) Select(box, anchor, index);
        }
        void Finish()
        {
            box.PreviewMouseMove -= Follow;
            box.MouseLeftButtonUp -= Up;
            box.LostMouseCapture -= Lost;
            if (box.IsMouseCaptured) box.ReleaseMouseCapture();
        }
        void Up(object sender, MouseButtonEventArgs e) => Finish();
        void Lost(object sender, MouseEventArgs e) => Finish();

        box.PreviewMouseMove += Follow;
        box.MouseLeftButtonUp += Up;
        box.LostMouseCapture += Lost;
    }

    /// <summary>Character offset under <paramref name="point"/> (nearest character), or -1 when it cannot be worked out.</summary>
    private static int IndexAt(TextBoxBase box, Point point)
    {
        switch (box)
        {
            case TextBox text:
                return text.GetCharacterIndexFromPoint(point, true);
            case RichTextBox rich:
                var position = rich.GetPositionFromPoint(point, true);
                return position is null ? -1 : rich.Document.ContentStart.GetOffsetToPosition(position);
            default:
                return -1;
        }
    }

    private static void Select(TextBoxBase box, int from, int to)
    {
        var start = Math.Min(from, to);
        var length = Math.Abs(to - from);
        if (box is TextBox text) text.Select(start, length);
        else if (box is RichTextBox rich)
        {
            var begin = rich.Document.ContentStart.GetPositionAtOffset(start);
            var end = rich.Document.ContentStart.GetPositionAtOffset(start + length);
            if (begin is not null && end is not null) rich.Selection.Select(begin, end);
        }
    }

    private static void Dismiss(AdornerLayer layer, SelectionAdorner adorner, TextBlock block, double opacity)
    {
        if (!adorner.IsLive) return;
        adorner.IsLive = false;
        layer.Remove(adorner);
        block.Opacity = opacity;
    }

    private static bool IsDescendant(DependencyObject parent, DependencyObject node)
    {
        for (var current = node; current is not null; current = Parent(current)) if (current == parent) return true;
        return false;
    }

    private static TextBox CreateBox(TextBlock block, string text)
    {
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = new FrameworkElementFactory(typeof(Border), "PART_ContentHost") };
        var box = new TextBox
        {
            Template = template,
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsInactiveSelectionHighlightEnabled = true,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            Background = Brushes.Transparent,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
            FontFamily = block.FontFamily,
            FontSize = block.FontSize,
            FontWeight = block.FontWeight,
            FontStyle = block.FontStyle,
            FontStretch = block.FontStretch,
            Foreground = block.Foreground,
            TextWrapping = block.TextWrapping == TextWrapping.NoWrap ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextAlignment = block.TextAlignment,
            Effect = block.Effect,
            SelectionOpacity = 0.55,
            ContextMenu = BuildBoxMenu(),
        };
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentFillColorSelectedTextBackgroundBrush");
        TextBlock.SetLineHeight(box, block.LineHeight);
        TextBlock.SetLineStackingStrategy(box, block.LineStackingStrategy);
        return box;
    }

    /// <summary>Hosts the overlay box exactly over the TextBlock it replaces.</summary>
    private sealed class SelectionAdorner : Adorner
    {
        private readonly UIElement _box;

        public SelectionAdorner(UIElement adorned, UIElement box) : base(adorned)
        {
            _box = box;
            AddVisualChild(box);
        }

        public bool IsLive { get; set; } = true;
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _box;

        protected override Size MeasureOverride(Size constraint)
        {
            _box.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _box.Arrange(new Rect(AdornedElement.RenderSize));
            return AdornedElement.RenderSize;
        }
    }

    // ───────────── Text with bold / italic / links ─────────────

    /// <summary>True when part of the text is formatted differently from the rest, so a plain text box would lose it.</summary>
    public static bool IsMixed(TextBlock block) => block.Inlines.Any(i => Differs(i, block));

    private static bool Differs(Inline inline, TextBlock owner) => inline switch
    {
        LineBreak => false,
        Span => true,
        _ => inline.FontWeight != owner.FontWeight || inline.FontStyle != owner.FontStyle || inline.FontSize != owner.FontSize
             || !Equals(inline.FontFamily, owner.FontFamily) || !Equals(inline.Foreground, owner.Foreground) || inline.TextDecorations is { Count: > 0 },
    };

    private static Inline CloneInline(Inline source)
    {
        Inline copy = source switch
        {
            LineBreak => new LineBreak(),
            Run run => new Run(run.Text),
            Span => new Span(),
            _ => new Run(new TextRange(source.ContentStart, source.ContentEnd).Text),
        };
        if (source is Span span && copy is Span target)
            foreach (var child in span.Inlines.ToList()) target.Inlines.Add(CloneInline(child));
        copy.FontWeight = source.FontWeight;
        copy.FontStyle = source.FontStyle;
        copy.FontSize = source.FontSize;
        copy.FontFamily = source.FontFamily;
        copy.Foreground = source.Foreground;
        if (source.TextDecorations is { Count: > 0 } decorations) copy.TextDecorations = decorations;
        return copy;
    }

    /// <summary>Read-only rich text over a TextBlock whose runs are formatted differently (bold, italic, links).</summary>
    private static RichTextBox CreateRichBox(TextBlock block)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var inline in block.Inlines.ToList()) paragraph.Inlines.Add(CloneInline(inline));
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight, FontStyle = block.FontStyle,
            Foreground = block.Foreground, TextAlignment = block.TextAlignment, LineHeight = block.LineHeight, LineStackingStrategy = block.LineStackingStrategy,
        };
        if (block.TextWrapping == TextWrapping.NoWrap) document.PageWidth = 100000;
        var template = new ControlTemplate(typeof(RichTextBox)) { VisualTree = new FrameworkElementFactory(typeof(Border), "PART_ContentHost") };
        var box = new RichTextBox
        {
            Template = template, Document = document, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsInactiveSelectionHighlightEnabled = true,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Margin = new Thickness(0), MinHeight = 0, MinWidth = 0,
            Background = Brushes.Transparent, FocusVisualStyle = null, Cursor = Cursors.IBeam, Effect = block.Effect, SelectionOpacity = 0.55,
            ContextMenu = BuildBoxMenu(),
        };
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentFillColorSelectedTextBackgroundBrush");
        return box;
    }

    // ───────────── Selecting across several texts ─────────────

    /// <summary>
    /// While the mouse is held down and dragged out of the text it started in, every text it passes over is highlighted too, and
    /// Copy (Ctrl+C or the menu) copies all of them in reading order. Whole texts are selected, not partial lines.
    /// </summary>
    private sealed class RangeSelection
    {
        private readonly TextBlock _anchor;
        private readonly TextBoxBase _box;
        private readonly List<(TextBlock Block, AdornerLayer Layer, SelectionAdorner Adorner, double Opacity)> _shown = new();
        private string _text = "";
        private Window? _window;

        public RangeSelection(TextBlock anchor, TextBoxBase box)
        {
            _anchor = anchor;
            _box = box;
            box.PreviewMouseMove += OnMove;
            box.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(OnCommand), true);
        }

        /// <summary>True while other texts, besides the one that was pressed, are part of the selection.</summary>
        public bool IsActive => _shown.Count > 0;

        private void OnCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!IsActive || e.Command != ApplicationCommands.Copy) return;
            PutOnClipboard(_text, _anchor);
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var root = PageRootOf(_anchor);
            Update(root, e.GetPosition(root), out _);
        }

        /// <summary>Extends the selection from the anchor text to the text nearest <paramref name="point"/> (page coordinates).</summary>
        /// <returns>True when several texts are now selected; <paramref name="text"/> is what Copy will put on the clipboard.</returns>
        public bool Update(FrameworkElement root, Point point, out string text)
        {
            text = "";
            Clear();
            var anchorRect = new Rect(_anchor.TranslatePoint(new Point(0, 0), root), _anchor.RenderSize);
            if (anchorRect.Contains(point)) return false;

            var candidates = Eligible(root, _anchor);
            var end = candidates.OrderBy(c => Distance(c.Rect, point)).FirstOrDefault();
            if (end.Block is null || ReferenceEquals(end.Block, _anchor)) return false;
            var from = candidates.FindIndex(c => ReferenceEquals(c.Block, _anchor));
            var to = candidates.FindIndex(c => ReferenceEquals(c.Block, end.Block));
            if (from < 0 || to < 0) return false;
            var range = candidates.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToList();

            foreach (var (block, rect) in range)
            {
                if (ReferenceEquals(block, _anchor)) continue;
                if (AdornerLayer.GetAdornerLayer(block) is not { } layer) continue;
                var box = CreateBox(block, TextOf(block));
                box.Focusable = false;
                box.SelectAll();
                var adorner = new SelectionAdorner(block, box);
                var opacity = block.Opacity;
                block.Opacity = 0;
                layer.Add(adorner);
                _shown.Add((block, layer, adorner, opacity));
            }
            _box.SelectAll();
            RefreshSelection(_box);
            _text = Join(range.Select(c => (c.Rect.Y, c.Rect.X, TextOf(c.Block))));
            if (_window is null && Window.GetWindow(_anchor) is { } window)
            {
                _window = window;
                window.PreviewMouseDown += OnWindowMouseDown;
            }
            text = _text;
            return true;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsDescendant(_box, source)) return;
            Clear();
            if (_box.IsKeyboardFocusWithin) Keyboard.ClearFocus();   // the overlay then goes away through its focus handler
        }

        public void Clear()
        {
            foreach (var (block, layer, adorner, opacity) in _shown)
            {
                layer.Remove(adorner);
                block.Opacity = opacity;
            }
            _shown.Clear();
            _text = "";
            if (_window is not null) { _window.PreviewMouseDown -= OnWindowMouseDown; _window = null; }
        }

        /// <summary>A text box that is being dragged over does not always repaint a selection set from code; nudge it.</summary>
        private static void RefreshSelection(TextBoxBase box)
        {
            box.InvalidateVisual();
            box.UpdateLayout();
            box.Dispatcher.BeginInvoke(() => box.SelectAll(), DispatcherPriority.Render);
        }

        private static double Distance(Rect rect, Point p)
        {
            var dx = Math.Max(Math.Max(rect.Left - p.X, 0), p.X - rect.Right);
            var dy = Math.Max(Math.Max(rect.Top - p.Y, 0), p.Y - rect.Bottom);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>Drag-selection across texts, driven directly (the mouse handler calls the same code). Used by tests.</summary>
    public static bool TrySelectRange(TextBlock anchor, TextBoxBase overlay, FrameworkElement root, Point point, out string text)
    {
        text = "";
        return Ranges.TryGetValue(overlay, out var range) && range.Update(root, point, out text);
    }

    /// <summary>The texts on a page that the mouse may select, in reading order (the anchor is included even while hidden).</summary>
    private static List<(TextBlock Block, Rect Rect)> Eligible(FrameworkElement root, TextBlock anchor)
    {
        var found = new List<(TextBlock Block, Rect Rect)>();
        void Visit(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false }) return;
            if (node is TextBlock block)
            {
                if (block.ActualWidth >= 1 && block.ActualHeight >= 1 && (ReferenceEquals(block, anchor) || block.Opacity > 0)
                    && !IsClickTarget(block) && !string.IsNullOrWhiteSpace(TextOf(block)))
                    found.Add((block, new Rect(block.TranslatePoint(new Point(0, 0), root), block.RenderSize)));
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root);
        return found.OrderBy(f => Math.Round(f.Rect.Y / 8)).ThenBy(f => f.Rect.X).ToList();
    }

    // ───────────── Rules ─────────────

    /// <summary>True when the text sits in a control that must keep its click, drag or toggle behaviour.</summary>
    public static bool IsClickTarget(DependencyObject element)
    {
        for (var node = element; node is not null; node = Parent(node))
            if (node is ButtonBase or ListBoxItem or ComboBoxItem or ComboBox or MenuItem or TabItem or TreeViewItem or Slider or Thumb or ScrollBar or TextBox)
                return true;
        return false;
    }

    private static bool IsInsideOverlay(DependencyObject element) => Window.GetWindow(element) is OverlayWindow;

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    // ───────────── Text helpers (public so they can be unit tested) ─────────────

    /// <summary>The visible text of a TextBlock, including inline runs and line breaks.</summary>
    public static string TextOf(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n');

    /// <summary>Every visible piece of text under <paramref name="root"/> in reading order (top to bottom, then left to right).</summary>
    public static string CollectText(FrameworkElement root)
    {
        var pieces = new List<(double Y, double X, string Text)>();
        Collect(root, root, pieces);
        return Join(pieces);
    }

    private static string Join(IEnumerable<(double Y, double X, string Text)> pieces)
    {
        var builder = new StringBuilder();
        double? rowY = null;
        foreach (var piece in pieces.OrderBy(p => Math.Round(p.Y / 8)).ThenBy(p => p.X))
        {
            var sameRow = rowY is { } y && Math.Abs(Math.Round(piece.Y / 8) - Math.Round(y / 8)) < 1;
            if (builder.Length > 0) builder.Append(sameRow ? "\t" : Environment.NewLine);
            builder.Append(piece.Text.Trim());
            rowY = piece.Y;
        }
        return builder.ToString();
    }

    private static void Collect(DependencyObject node, FrameworkElement root, List<(double, double, string)> pieces)
    {
        if (node is UIElement { IsVisible: false }) return;
        if (node is FrameworkElement { Opacity: 0 } and not TextBlock) return;
        string? text = node switch
        {
            TextBlock block => TextOf(block),
            TextBox box => box.Text,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(text) && node is FrameworkElement element)
        {
            var origin = element.TranslatePoint(new Point(0, 0), root);
            pieces.Add((origin.Y, origin.X, text));
        }
        if (node is TextBlock) return; // inlines are already inside its text
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i), root, pieces);
    }

    /// <summary>The element that represents "this page": the nearest view (UserControl), otherwise the whole window.</summary>
    public static FrameworkElement PageRootOf(DependencyObject element)
    {
        for (var node = element; node is not null; node = Parent(node))
            if (node is UserControl view) return view;
        return Window.GetWindow(element)?.Content as FrameworkElement ?? (FrameworkElement)element;
    }

    // ───────────── Menus ─────────────

    private static ContextMenu BuildBlockMenu()
    {
        var copy = new MenuItem { Header = "Copy" };
        var copyAll = new MenuItem { Header = "Copy everything on this page" };
        var menu = new ContextMenu { Items = { copy, copyAll } };
        copy.Click += (_, _) => { if (menu.PlacementTarget is TextBlock block) PutOnClipboard(TextOf(block), block); };
        copyAll.Click += (_, _) => { if (menu.PlacementTarget is DependencyObject target) PutOnClipboard(CollectText(PageRootOf(target)), target as UIElement); };
        return menu;
    }

    private static ContextMenu BuildBoxMenu()
    {
        var copy = new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, InputGestureText = "Ctrl+C" };
        var all = new MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll, InputGestureText = "Ctrl+A" };
        var copyAll = new MenuItem { Header = "Copy everything on this page" };
        var menu = new ContextMenu { Items = { copy, all, new Separator(), copyAll } };
        copyAll.Click += (_, _) => { if (menu.PlacementTarget is DependencyObject target) PutOnClipboard(CollectText(PageRootOf(target)), target as UIElement); };
        return menu;
    }

    private static void PutOnClipboard(string text, UIElement? near)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); Confirm(near); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(30); } // clipboard briefly held by another app
        }
    }

    /// <summary>A small "Copied" bubble next to the text, gone after a moment.</summary>
    private static void Confirm(UIElement? near)
    {
        if (near is null || !near.IsVisible || Application.Current is null) return;
        var fill = Application.Current.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DimGray;
        var ink = Application.Current.TryFindResource("AccentOn") as Brush ?? Brushes.White;
        var bubble = new Popup
        {
            PlacementTarget = near, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, IsHitTestVisible = false,
            Child = new Border
            {
                Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(6), Margin = new Thickness(4), Background = fill,
                Child = new TextBlock { Text = "Copied", Foreground = ink, FontWeight = FontWeights.SemiBold },
            },
            IsOpen = true,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, _) => { timer.Stop(); bubble.IsOpen = false; };
        timer.Start();
    }
}
