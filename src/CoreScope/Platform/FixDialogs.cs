using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreScope.Core.Fixes;

namespace CoreScope.Platform;

/// <summary>The two small windows fixes can open: a yes/no confirmation and a step-by-step guide. Built in code, themed like the app.</summary>
internal static class FixDialogs
{
    private static Window Shell(string title, double width)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null,
        };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        window.SetResourceReference(Control.FontSizeProperty, "FontBody");
        window.SetResourceReference(Control.BackgroundProperty, "WindowBase");
        WindowBackdrop.Attach(window);
        return window;
    }

    private static TextBlock Text(string text, string style, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        if (Application.Current.TryFindResource(style) is Style s) block.Style = s;
        return block;
    }

    private static Button MakeButton(string label, bool primary)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 8, 16, 8), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        if (primary && Application.Current.TryFindResource("PrimaryButton") is Style style) button.Style = style;
        return button;
    }

    // ───────────── Confirmation ─────────────

    /// <summary>The "what this will do" box: a heading, the change in words, and the command in a monospaced line.</summary>
    private static Border PreviewBox(string preview)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "What this will do", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var line in preview.Split('\n'))
        {
            var isCommand = line.StartsWith("Command:", StringComparison.Ordinal) || line.StartsWith("Change:", StringComparison.Ordinal);
            var block = Text(line, isCommand ? "Caption" : "BodyText", new Thickness(0, 0, 0, 2));
            if (isCommand) block.FontFamily = new FontFamily("Consolas");
            panel.Children.Add(block);
        }
        var box = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
        box.SetResourceReference(Border.BackgroundProperty, "AccentWash");
        box.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
        return box;
    }

    public static bool Confirm(string title, string message, string yes, string? preview = null)
    {
        var window = Shell("CoreScope", 460);
        var ok = false;
        var yesButton = MakeButton(yes, primary: true);
        var cancel = MakeButton("Cancel", primary: false);
        yesButton.Click += (_, _) => { ok = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        cancel.IsCancel = true;
        yesButton.IsDefault = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(yesButton);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Text(title, "TitleText", new Thickness(0, 0, 0, 12)));
        body.Children.Add(Text(message, "BodyText"));
        if (!string.IsNullOrWhiteSpace(preview)) body.Children.Add(PreviewBox(preview));
        body.Children.Add(buttons);
        window.Content = body;
        window.ShowDialog();
        return ok;
    }

    // ───────────── Guide ─────────────

    public static void Guide(Guide guide, Func<FixAction, Task<FixResult>> run)
    {
        var window = Shell(guide.Title, 580);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Text(guide.Title, "TitleText", new Thickness(0, 0, 0, 8)));
        body.Children.Add(Text(guide.Intro, "BodySecondary", new Thickness(0, 0, 0, 16)));

        var number = 1;
        foreach (var step in guide.Steps)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentWash");
            badge.Child = new TextBlock { Text = number++.ToString(System.Globalization.CultureInfo.InvariantCulture), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var text = Text(step, "BodyText");
            Grid.SetColumn(text, 1);
            row.Children.Add(badge);
            row.Children.Add(text);
            body.Children.Add(row);
        }

        var status = Text("", "Caption", new Thickness(0, 8, 0, 0));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in guide.Actions)
        {
            var button = MakeButton(action.Label, primary: action.Primary);
            button.Margin = new Thickness(8, 0, 0, 8);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    var result = await run(action);
                    status.Text = result.Message;
                }
                finally { button.IsEnabled = true; }
            };
            buttons.Children.Add(button);
        }
        var close = MakeButton("Close", primary: false);
        close.IsCancel = true;
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(close);

        body.Children.Add(buttons);
        body.Children.Add(status);
        window.Content = body;
        window.Show();   // not modal: the guide stays open while you follow the steps
    }
}
