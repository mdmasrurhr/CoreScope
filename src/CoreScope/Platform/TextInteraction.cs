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
    /// (click target, empty, hidden, no adorner layer). When <paramref name="mouse"/> is given, the click is forwarded so a drag
    /// that began on the text carries on selecting.
    /// </summary>
    public static TextBox? BeginSelection(TextBlock block, MouseButtonEventArgs? mouse = null)
    {
        if (!block.IsVisible || block.ActualWidth < 1 || block.ActualHeight < 1) return null;
        if (IsClickTarget(block) || IsInsideOverlay(block)) return null;
        var text = TextOf(block);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var layer = AdornerLayer.GetAdornerLayer(block);
        if (layer is null) return null;

        var box = CreateBox(block, text);
        var adorner = new SelectionAdorner(block, box);
        var previousOpacity = block.Opacity;
        block.Opacity = 0; // the overlay draws the same text; hide the original so it is not drawn twice
        layer.Add(adorner);
        box.LostKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is DependencyObject target && (target == box || IsDescendant(box, target))) return; // e.g. its own context menu
            Dismiss(layer, adorner, block, previousOpacity);
        };
        block.Unloaded += (_, _) => Dismiss(layer, adorner, block, previousOpacity);

        layer.UpdateLayout();
        box.Focus();
        if (mouse is not null)
        {
            var forwarded = new MouseButtonEventArgs(mouse.MouseDevice, mouse.Timestamp, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = box };
            box.RaiseEvent(forwarded);
        }
        return box;
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
        private readonly TextBox _box;

        public SelectionAdorner(UIElement adorned, TextBox box) : base(adorned)
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
