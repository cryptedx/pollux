using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.App.Views;

/// <summary>Home: Bookmarks, Recent and drives at a glance.</summary>
internal static class HomeView
{
    public static void Render(StackPanel panel, IPaneHost host, PaneView pane)
    {
        panel.Children.Clear();
        var s = host.Services;

        panel.Children.Add(Heading("Bookmarks"));
        var wrap = Grid();
        var bookmarks = s.Bookmarks.Flat.ToList();
        for (var i = 0; i < bookmarks.Count; i++)
        {
            var b = bookmarks[i];
            AddTile(wrap, b.Name, b.Path, i < 9 ? $"^⇧{i + 1}" : "", () => pane.Navigate(b.Path), folder: Directory.Exists(b.Path));
        }

        if (bookmarks.Count == 0) panel.Children.Add(Muted("No bookmarks yet — press Ctrl+D in any folder."));
        panel.Children.Add(wrap);

        var recent = s.Recent.Ranked(DateTime.Now).Take(12).ToList();
        if (recent.Count > 0)
        {
            panel.Children.Add(Heading("Recent"));
            var rg = Grid();
            foreach (var r in recent) AddTile(rg, WinPath.Name(r.Path), r.Path, Format.Relative(r.LastVisit, DateTime.Now), () => pane.Navigate(r.Path), folder: true);
            panel.Children.Add(rg);
        }

        panel.Children.Add(Heading("Drives"));
        // A static link: Home never lists This PC, and never shows Network or servers (ADR 0010).
        var places = Grid();
        AddTile(places, "This PC", "drives, phones and cameras", "", () => pane.Navigate(WinPath.ThisPc), folder: false, glyph: "🖥");
        panel.Children.Add(places);
        foreach (var d in Drives.Current)
        {
            var root = d.Root;
            panel.Children.Add(DriveRow(d, () => pane.Navigate(root)));
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 60,
        Foreground = Theme.Brush("PolluxMutedBrush"),
        Margin = new Thickness(2, 14, 0, 4),
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 12,
        Foreground = Theme.Brush("PolluxMutedBrush"),
        Margin = new Thickness(2, 0, 0, 0),
    };

    private static FontFamily Mono => Ui.Mono;

    private static VariableSizedWrapGrid Grid() => new()
    {
        Orientation = Orientation.Horizontal,
        ItemWidth = 210,
        ItemHeight = 58,
    };

    private static void AddTile(VariableSizedWrapGrid grid, string title, string path, string hint, Action onClick, bool folder, string? glyph = null)
    {
        var icon = glyph is not null
            ? new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Center }
            : folder
            ? (FrameworkElement)new Border { Width = 18, Height = 13, CornerRadius = new CornerRadius(2, 4, 3, 3), Background = Theme.Brush("PolluxFolderBrush"), VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = "▤", VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var hintText = new TextBlock { Text = hint, FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), Margin = new Thickness(6, 0, 0, 0) };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(hintText, 1);
        top.Children.Add(hintText);
        text.Children.Add(top);
        text.Children.Add(new TextBlock { Text = path, FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = new Button
        {
            Content = row,
            Style = Ui.FlatButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 6, 10, 6),
            Background = Theme.Brush("PolluxSurface2Brush"),
        };
        ToolTipService.SetToolTip(button, path);
        button.Click += (_, _) => onClick();
        grid.Children.Add(button);
    }

    private static Button DriveRow(DriveSnapshot d, Action onClick)
    {
        var pct = d.Total == 0 ? 0 : (double)(d.Total - d.Free) / d.Total;
        var panel = new StackPanel { Spacing = 4, Width = 420 };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = $"{d.Label} ({d.Root.TrimEnd('\\')})", FontSize = 13 });
        var free = new TextBlock
        {
            Text = d.Ready ? $"{Format.Size(d.Free)} free of {Format.Size(d.Total)}" : "not available",
            FontFamily = Mono,
            FontSize = 11,
            Foreground = Theme.Brush("PolluxMutedBrush"),
        };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(free, 1);
        top.Children.Add(free);
        panel.Children.Add(top);
        panel.Children.Add(Bar(pct, 420, 6, pct > 0.9 ? "PolluxBadBrush" : "PolluxAccentBrush"));
        var b = new Button
        {
            Content = panel,
            Style = Ui.FlatButton,
            Padding = new Thickness(10, 6, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Background = Theme.Brush("PolluxSurface2Brush"),
            Opacity = d.Ready ? 1 : 0.6,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Grid Bar(double fraction, double width, double height, string brush = "PolluxAccentBrush")
    {
        var g = new Grid { Width = width, Height = height, CornerRadius = new CornerRadius(height / 2), Background = Theme.Brush("PolluxLineBrush"), HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(new Border { Width = Math.Clamp(fraction, 0, 1) * width, HorizontalAlignment = HorizontalAlignment.Left, Background = Theme.Brush(brush), CornerRadius = new CornerRadius(height / 2) });
        return g;
    }
}
