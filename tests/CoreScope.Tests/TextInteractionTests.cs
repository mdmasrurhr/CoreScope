using System.Threading;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CoreScope.Platform;
using Xunit;

namespace CoreScope.Tests;

public class TextSelectionTests
{
    /// <summary>Runs <paramref name="body"/> on an STA thread with a real (shown) window so Loaded/Layout behave like the app.</summary>
    private static void OnUiThread(System.Action<Window> body)
    {
        System.Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                TextInteraction.Register();   // like the app: before any window exists
                var window = new Window { Width = 500, Height = 400, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000 };
                window.Show();
                try { body(window); } finally { window.Close(); }
            }
            catch (System.Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new System.Exception("UI test failed: " + failure.Message, failure);
    }

    private static void RightClick(UIElement element) =>
        element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = element });

    /// <summary>Lets pending Loaded events run (they are queued at Loaded priority after the window shows).</summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    [Fact]
    public void EveryTextGetsTheCopyMenu_ButOnlyPlainTextCanBeSelectedByMouse()
    {
        OnUiThread(window =>
        {
            TextInteraction.Register();
            var plain = new TextBlock { Text = "Plain text" };
            var inButton = new TextBlock { Text = "Click me" };
            var inList = new ListBoxItem { Content = new TextBlock { Text = "Row" } };
            window.Content = new StackPanel { Children = { plain, new Button { Content = inButton }, inList } };
            Settle(window);

            foreach (var block in new[] { plain, inButton, (TextBlock)inList.Content }) RightClick(block);   // menu is attached on right-click
            Assert.NotNull(plain.ContextMenu);
            Assert.NotNull(inButton.ContextMenu);                          // right-click Copy works everywhere
            Assert.NotNull(((TextBlock)inList.Content).ContextMenu);

            Assert.NotNull(TextInteraction.BeginSelection(plain));           // dragging works on plain text
            Assert.Null(TextInteraction.BeginSelection(inButton));           // but never fights a click target
            Assert.Null(TextInteraction.BeginSelection((TextBlock)inList.Content));
        });
    }

    [Fact]
    public void SelectionOverlay_ShowsTheSameTextAndGoesAwayWhenFocusMovesOn()
    {
        OnUiThread(window =>
        {
            var block = new TextBlock { Text = "Select me please", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var other = new TextBox { Text = "elsewhere" };
            window.Content = new StackPanel { Children = { block, other } };
            window.UpdateLayout();

            var box = TextInteraction.BeginSelection(block);
            Assert.NotNull(box);
            Assert.Equal("Select me please", box!.Text);
            Assert.True(box.IsReadOnly);
            Assert.Equal(18, box.FontSize);
            Assert.Equal(FontWeights.SemiBold, box.FontWeight);
            Assert.Equal(0, block.Opacity);                                // original hidden while the overlay draws

            box.SelectAll();
            Assert.Equal("Select me please", box.SelectedText);

            other.Focus();                                                 // click elsewhere
            window.UpdateLayout();
            Assert.Equal(1, block.Opacity);                                // original is back
        });
    }

    [Fact]
    public void ExistingContextMenusAreRespected()
    {
        OnUiThread(window =>
        {
            TextInteraction.Register();
            var own = new ContextMenu();
            var block = new TextBlock { Text = "Mine", ContextMenu = own };
            window.Content = block;
            window.UpdateLayout();
            RightClick(block);
            Assert.Same(own, block.ContextMenu);
        });
    }

    [Fact]
    public void TextOf_IncludesInlinesAndLineBreaks()
    {
        OnUiThread(_ =>
        {
            var block = new TextBlock();
            block.Inlines.Add(new Run("Hello "));
            block.Inlines.Add(new Run("world"));
            block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new Run("again"));
            Assert.Equal("Hello world\r\nagain", TextInteraction.TextOf(block).Replace("\n", "\r\n").Replace("\r\r", "\r"));
        });
    }

    [Fact]
    public void CollectText_ReadsTopToBottomThenLeftToRight_AndSkipsHidden()
    {
        OnUiThread(window =>
        {
            var grid = new Grid { Width = 400, Height = 200 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            void Put(string text, int row, int col, Visibility v = Visibility.Visible)
            {
                var block = new TextBlock { Text = text, Visibility = v };
                Grid.SetRow(block, row); Grid.SetColumn(block, col);
                grid.Children.Add(block);
            }
            Put("D bottom right", 1, 1);
            Put("A top left", 0, 0);
            Put("hidden", 0, 0, Visibility.Collapsed);
            Put("B top right", 0, 1);
            Put("C bottom left", 1, 0);
            window.Content = grid;
            window.UpdateLayout();

            var text = TextInteraction.CollectText(grid).Replace("\r\n", "\n");
            Assert.Equal("A top left\tB top right\nC bottom left\tD bottom right", text);
        });
    }

    [Fact]
    public void PageRoot_IsTheNearestUserControl()
    {
        OnUiThread(window =>
        {
            var inner = new TextBlock { Text = "x" };
            var view = new UserControl { Content = new StackPanel { Children = { inner } } };
            window.Content = new Grid { Children = { view } };
            window.UpdateLayout();
            Assert.Same(view, TextInteraction.PageRootOf(inner));
        });
    }
}
