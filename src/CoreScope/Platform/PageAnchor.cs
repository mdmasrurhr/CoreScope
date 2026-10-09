using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoreScope.Platform;

/// <summary>Scrolls the page on screen to a named section (the card whose title matches), so a fix button lands exactly where the setting is.</summary>
internal static class PageAnchor
{
    /// <summary>Tries now, then again shortly after: the page may still be building its cards when it was just opened.</summary>
    public static void ScrollTo(Window window, string title)
    {
        void Attempt() => ScrollNow(window, title);
        window.Dispatcher.BeginInvoke(Attempt, DispatcherPriority.ContextIdle);
        var retry = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        retry.Tick += (_, _) => { retry.Stop(); Attempt(); };
        retry.Start();
    }

    private static void ScrollNow(Window window, string title)
    {
        if (Find(window, title) is not { } block) return;
        // Bring the whole card into view, not just its title.
        DependencyObject target = block;
        for (var node = VisualTreeHelper.GetParent(block); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Border { Style: not null } card && card.Padding.Left > 0) { target = card; break; }
            if (node is ScrollViewer) break;
        }
        (target as FrameworkElement)?.BringIntoView();
    }

    private static TextBlock? Find(DependencyObject node, string title)
    {
        if (node is UIElement { IsVisible: false }) return null;
        if (node is TextBlock block && string.Equals(TextInteraction.TextOf(block).Trim(), title, StringComparison.OrdinalIgnoreCase)) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), title) is { } found) return found;
        return null;
    }
}
