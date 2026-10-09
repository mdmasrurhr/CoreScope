using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CoreScope.Core;
using CoreScope.Core.ViewModels;
using CoreScope.Platform;

namespace CoreScope;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        WindowBackdrop.Attach(this);
        Action applyLayout = ApplyLayout;
        ThemeManager.Applied += applyLayout;
        Closed += (_, _) => ThemeManager.Applied -= applyLayout;
        ApplyLayout();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Search.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SearchViewModel.IsOpen) && vm.Search.IsOpen)
                        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
                    if (e.PropertyName == nameof(SearchViewModel.Selected) && vm.Search.Selected is { } sel)
                        SearchResults.ScrollIntoView(sel);
                };
        };
    }

    // ───────────── Window layouts (Appearance → Layout) ─────────────
    // Sidebar: 240 px labelled sidebar. Rail: 72 px icon rail. TopBar: tabs across the top, content below.

    private string? _shownLayout;

    private void ApplyLayout()
    {
        var layout = ThemeManager.Effective.Layout;
        var changed = _shownLayout is not null && _shownLayout != layout;
        _shownLayout = layout;
        bool top = layout == "TopBar", rail = layout == "Rail";

        Root.ColumnDefinitions[0].Width = new GridLength(top ? 0 : rail ? 72 : 240);

        Place(SidebarHost, column: 0, columnSpan: top ? 2 : 1, row: 0, rowSpan: top ? 1 : 2);
        Place(ContentHost, column: top ? 0 : 1, columnSpan: top ? 2 : 1, row: top ? 1 : 0, rowSpan: top ? 1 : 2);

        SidebarLayer.BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        SidebarDock.Margin = top ? new Thickness(12, 8, 12, 8) : rail ? new Thickness(8, 16, 8, 12) : new Thickness(12, 16, 8, 12);

        DockPanel.SetDock(LogoPanel, top ? Dock.Left : Dock.Top);
        LogoPanel.Margin = top ? new Thickness(4, 0, 16, 0) : rail ? new Thickness(8, 4, 0, 16) : new Thickness(12, 4, 0, 24);
        LogoText.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;

        StatusPanel.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        StatusPanel.Margin = rail ? new Thickness(20, 8, 0, 0) : new Thickness(12, 8, 0, 0);
        StatusLabel.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;

        DockPanel.SetDock(SearchButton, top ? Dock.Right : Dock.Top);
        SearchButton.Margin = top ? new Thickness(8, 0, 0, 0) : new Thickness(4, 0, 4, 12);
        SearchButton.Width = top ? 176 : double.NaN;
        SearchLabel.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        SearchChip.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;

        // Sidebar shows section headings; the other two layouts are one flat list.
        var pages = (CollectionViewSource)Resources["GroupedPages"];
        bool grouped = layout == "Sidebar";
        if (grouped != (pages.GroupDescriptions.Count > 0))
        {
            pages.GroupDescriptions.Clear();
            if (grouped) pages.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
        }

        if (top)
        {
            var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
            NavList.ItemsPanel = new ItemsPanelTemplate(panel);
            NavList.Margin = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
        }
        else
        {
            NavList.ClearValue(ItemsControl.ItemsPanelProperty);
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Auto);
        }

        // A short fade so the navigation moving to another place reads as a change of look, not a glitch.
        if (changed && IsLoaded)
            Root.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
    }

    private void NavList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ThemeManager.Effective.Layout != "TopBar") return;
        if (FindScrollViewer(NavList) is not { } viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private static void Place(FrameworkElement element, int column, int columnSpan, int row, int rowSpan)
    {
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
    }

    private SearchViewModel? Search => (DataContext as MainViewModel)?.Search;

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Search is not { } search) return;
        switch (e.Key)
        {
            case Key.Down: search.Move(1); e.Handled = true; break;
            case Key.Up: search.Move(-1); e.Handled = true; break;
            case Key.Enter: search.GoCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: search.IsOpen = false; e.Handled = true; break;
        }
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Search?.GoCommand.Execute(null);

    // Click outside the panel closes the palette; clicks inside must not bubble to the backdrop.
    private void SearchBackdrop_MouseDown(object sender, MouseButtonEventArgs e) { if (Search is { } s) s.IsOpen = false; }
    private void SearchPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Search is { IsOpen: true } s) { s.IsOpen = false; e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// The designed size (1240×800, min 960×620) doesn't fit small or highly-scaled screens — e.g. 1366×768 at 125%
    /// leaves only ~1090×570 usable. Without this the title bar lands off-screen and the window can't be moved,
    /// resized or maximised. Shrink the size and minimums to the work area of the screen the window opens on.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea; // device-independent units, excludes the taskbar
        if (area.Width <= 0 || area.Height <= 0) return;

        MinWidth = Math.Min(MinWidth, Math.Max(640, area.Width - 16));
        MinHeight = Math.Min(MinHeight, Math.Max(400, area.Height - 16));
        Width = Math.Min(Width, area.Width * 0.94);
        Height = Math.Min(Height, area.Height * 0.94);

        // Very small screens: start maximised so nothing is cut off.
        if (area.Width < 1100 || area.Height < 640) WindowState = WindowState.Maximized;
    }
}
