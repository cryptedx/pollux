using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Files;

namespace Pollux.App.Views;

/// <summary>Batch Rename: options on top, live "old → new" preview below. Apply stays disabled while the plan has conflicts.</summary>
internal static class BatchRenameDialog
{
    /// <summary>Returns the plan to apply, or null when cancelled. <paramref name="exists"/> says whether a name is taken in the folder.</summary>
    public static async Task<RenamePlan?> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<RenameSource> sources, Func<string, bool> exists)
    {
        var find = new TextBox { PlaceholderText = "Find", FontFamily = Ui.Mono };
        var replace = new TextBox { PlaceholderText = "Replace with", FontFamily = Ui.Mono };
        var regex = new CheckBox { Content = "Regex" };
        var matchCase = new CheckBox { Content = "Match case" };
        var casing = new ComboBox { ItemsSource = new[] { "Keep case", "lowercase", "UPPERCASE", "Title Case" }, SelectedIndex = 0, MinWidth = 140 };
        var template = new TextBox { PlaceholderText = "Template, e.g. {name}_{n:000}{ext}", FontFamily = Ui.Mono };
        var start = new TextBox { Text = "1", Width = 60, FontFamily = Ui.Mono, Header = "Start" };
        var step = new TextBox { Text = "1", Width = 60, FontFamily = Ui.Mono, Header = "Step" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("PolluxMutedBrush") };
        var preview = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.None };

        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(find);
        panel.Children.Add(replace);
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        flags.Children.Add(regex);
        flags.Children.Add(matchCase);
        flags.Children.Add(casing);
        panel.Children.Add(flags);
        panel.Children.Add(template);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        numbers.Children.Add(start);
        numbers.Children.Add(step);
        panel.Children.Add(numbers);
        panel.Children.Add(status);
        panel.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = $"Batch rename {sources.Count} items",
            Content = panel,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            RequestedTheme = Theme.Current,
        };

        RenamePlan plan = BatchRename.Plan(sources, new(), exists);
        var error = new SolidColorBrush(Colors.IndianRed);

        void Refresh()
        {
            var options = new BatchRenameOptions(
                find.Text,
                replace.Text,
                regex.IsChecked == true,
                matchCase.IsChecked == true,
                casing.SelectedIndex switch { 1 => RenameCase.Lower, 2 => RenameCase.Upper, 3 => RenameCase.Title, _ => RenameCase.Keep },
                template.Text,
                int.TryParse(start.Text, out var s) ? s : 1,
                int.TryParse(step.Text, out var t) ? t : 1);
            plan = BatchRename.Plan(sources, options, exists);

            preview.Items.Clear();
            foreach (var item in plan.Items)
            {
                var line = item.IsChanged ? $"{item.OldName}  →  {item.NewName}" : item.OldName;
                if (item.Error is not null) line += $"   ({item.Error})";
                var text = new TextBlock { Text = line, FontFamily = Ui.Mono, FontSize = 12 };
                if (item.Error is not null) text.Foreground = error;
                else if (!item.IsChanged) text.Foreground = Theme.Brush("PolluxMutedBrush");
                preview.Items.Add(text);
            }

            var conflicts = plan.Items.Count(i => i.Error is not null);
            status.Text = plan.OptionsError
                ?? (conflicts > 0 ? $"{conflicts} conflict(s): resolve them to rename."
                : plan.HasChanges ? $"{plan.Items.Count(i => i.IsChanged)} item(s) will be renamed."
                : "Nothing to rename yet.");
            status.Foreground = plan.HasConflicts ? error : Theme.Brush("PolluxMutedBrush");
            dialog.IsPrimaryButtonEnabled = plan.CanApply;
        }

        find.TextChanged += (_, _) => Refresh();
        replace.TextChanged += (_, _) => Refresh();
        template.TextChanged += (_, _) => Refresh();
        start.TextChanged += (_, _) => Refresh();
        step.TextChanged += (_, _) => Refresh();
        regex.Click += (_, _) => Refresh();
        matchCase.Click += (_, _) => Refresh();
        casing.SelectionChanged += (_, _) => Refresh();
        find.Loaded += (_, _) => find.Focus(FocusState.Keyboard);
        Refresh();

        return await dialog.ShowAsync() == ContentDialogResult.Primary && plan.CanApply ? plan : null;
    }
}
