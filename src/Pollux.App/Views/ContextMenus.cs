using Microsoft.UI.Xaml.Controls;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Copilot;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.ShellNew;

namespace Pollux.App.Views;

/// <summary>Pollux's compact context menus. "More options" falls back to the shell for everything else.</summary>
internal static class ContextMenus
{
    public static MenuFlyout ForSelection(IPaneHost host, IReadOnlyList<FileItem> selection, string? folder, bool searchResults = false)
    {
        var menu = new MenuFlyout();
        void Add(string text, string command, string? key = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            var chord = host.Services.Keys.KeyFor(command);
            if (chord is not null) item.KeyboardAcceleratorTextOverride = chord.Value.ToString();
            item.Click += (_, _) => host.Execute(command);
            menu.Items.Add(item);
        }

        void Sep() => menu.Items.Add(new MenuFlyoutSeparator());

        if (selection.Count > 0)
        {
            Add("Open", "file.open");
            if (selection.Count == 1 && !selection[0].IsDirectory) Add("Open with…", "file.openWith");
            Add("Open in VS Code", "integration.vscode");
            if (selection.Count == 1 && selection[0].IsDirectory) Add("Terminal here", "terminal.open");
            Sep();
            Add("Cut", "file.cut");
            Add("Copy", "file.copy");
            Add("Copy to other pane", "file.copyToOther");
            Add("Move to other pane", "file.moveToOther");
            Sep();
            Add("Create shortcut", "file.createShortcut");
            Add("Create symbolic link in other pane", "file.createSymlink");
            if (selection.All(i => i.IsDirectory)) Add("Create junction in other pane", "file.createJunction");
            Sep();
            Add("Copy path", "file.copyPath");
            Add("Copy path (quoted)", "file.copyPathQuoted");
            Add("Copy path (forward slashes)", "file.copyPathForward");
            Add("Copy name", "file.copyName");
            Sep();
            if (selection.Count == 1) Add("Rename", "file.rename");
            else if (selection.Count > 1 && !searchResults) Add("Batch rename…", "file.batchRename");
            Add("Delete", "file.delete");
            Add("Add bookmark", "bookmark.addSelection");
            if (CopilotAvailability.IsAvailable(host.Services.Settings.Copilot, MainWindow.IsElevated))
            {
                var sub = new MenuFlyoutSubItem { Text = "Copilot" };
                foreach (var a in SelectionActions.For(host.Services.Settings.Copilot))
                {
                    var item = new MenuFlyoutItem { Text = a.Title };
                    var id = a.CommandId;
                    item.Click += (_, _) => host.Execute(id);
                    sub.Items.Add(item);
                }

                menu.Items.Add(sub);
            }

            Sep();
            Add("Properties", "file.properties");
            Add("More options (Windows menu)", "file.shellMenu");
            Add("Reveal in Explorer", "file.showInExplorer");
        }
        else if (folder is not null)
        {
            Add("Paste", "file.paste");
            menu.Items.Add(NewMenu(host, folder));
            Sep();
            Add("Terminal here", "terminal.open");
            Add("Open in VS Code", "integration.vscode");
            Add("Add bookmark", "bookmark.add");
            Add("Copy path", "file.copyPath");
            Sep();
            Add("Refresh", "view.refresh");
            Add("Properties", "file.properties");
        }

        return menu;
    }

    /// <summary>"New >": Folder, Text file, then the registered templates (not yet read at the first right-click: then only the first two).</summary>
    private static MenuFlyoutSubItem NewMenu(IPaneHost host, string folder)
    {
        var sub = new MenuFlyoutSubItem { Text = "New" };
        void AddCommand(string text, string command)
        {
            var item = new MenuFlyoutItem { Text = text };
            var chord = host.Services.Keys.KeyFor(command);
            if (chord is not null) item.KeyboardAcceleratorTextOverride = chord.Value.ToString();
            item.Click += (_, _) => host.Execute(command);
            sub.Items.Add(item);
        }

        AddCommand("Folder", "file.newFolder");
        AddCommand("Text file", "file.newFile");
        var shellNew = host.Services.ShellNew;
        if (shellNew.Loaded is not { } entries)
        {
            _ = shellNew.EntriesAsync();
            return sub;
        }

        var templates = entries.Where(e => !e.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (templates.Count == 0) return sub;
        sub.Items.Add(new MenuFlyoutSeparator());
        var icons = new List<(MenuFlyoutItem Item, ShellNewEntry Entry)>();
        foreach (var entry in templates)
        {
            var item = new MenuFlyoutItem { Text = entry.DisplayName };
            var e = entry;
            item.Click += (_, _) => host.NewFromTemplate(e);
            sub.Items.Add(item);
            icons.Add((item, entry));
        }

        // The icons need the display scale, known once the menu is shown.
        sub.Loaded += (_, _) =>
        {
            var scale = sub.XamlRoot?.RasterizationScale ?? 1;
            foreach (var (item, entry) in icons)
            {
                var file = "New" + entry.Extension;
                var key = Icons.KeyFor(new FileEntry(file, Path.Combine(folder, file), false, 0, default, FileAttributes.Normal), 16, scale);
                var image = Icons.Get(key, sub.DispatcherQueue, loaded => item.Icon = new ImageIcon { Source = loaded });
                if (image is not null) item.Icon = new ImageIcon { Source = image };
            }
        };
        return sub;
    }

    /// <summary>
    /// The menu of a read-only location (a zip, a server's shares, a shell folder): open, copy out and paths, nothing that
    /// would change it. Shell folders and shares also get Properties and the full Windows menu.
    /// </summary>
    public static MenuFlyout ForReadOnly(IPaneHost host, IReadOnlyList<FileItem> selection, LocationKind kind)
    {
        var menu = new MenuFlyout();
        void Add(string text, string command)
        {
            var item = new MenuFlyoutItem { Text = text };
            var chord = host.Services.Keys.KeyFor(command);
            if (chord is not null) item.KeyboardAcceleratorTextOverride = chord.Value.ToString();
            item.Click += (_, _) => host.Execute(command);
            menu.Items.Add(item);
        }

        void Sep() => menu.Items.Add(new MenuFlyoutSeparator());

        var zip = kind == LocationKind.Zip;
        var shell = kind != LocationKind.Zip;
        if (selection.Count > 0)
        {
            Add("Open", "file.open");
            if (selection.Count == 1 && !selection[0].IsDirectory) Add("Open with…", "file.openWith");
            if (Locations.Access(kind).CanCopyOut)
            {
                Sep();
                Add("Copy", "file.copy");
                Add(zip ? "Extract to other pane" : "Copy to other pane", "file.copyToOther");
            }

            Sep();
            Add("Copy path", "file.copyPath");
            Add("Copy name", "file.copyName");
            if (shell)
            {
                Sep();
                Add("Properties", "file.properties");
                Add("More options (Windows menu)", "file.shellMenu");
            }
        }
        else
        {
            Add("Copy path", "file.copyPath");
            Add("Refresh", "view.refresh");
            if (kind == LocationKind.Shell) Add("Properties", "file.properties");
        }

        return menu;
    }

    /// <summary>The Recycle Bin's menu: its entries can only be restored or deleted for good.</summary>
    public static MenuFlyout ForRecycleBin(IPaneHost host, IReadOnlyList<FileItem> selection)
    {
        var menu = new MenuFlyout();
        // keyOf: the command whose shortcut reaches this one there (Delete runs "delete permanently" in the bin).
        void Add(string text, string command, string? keyOf = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            var chord = host.Services.Keys.KeyFor(keyOf ?? command);
            if (chord is not null) item.KeyboardAcceleratorTextOverride = chord.Value.ToString();
            item.Click += (_, _) => host.Execute(command);
            menu.Items.Add(item);
        }

        if (selection.Count > 0)
        {
            Add("Restore", "recycle.restore");
            Add("Delete permanently…", "recycle.deletePermanent", "file.delete");
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        Add("Empty Recycle Bin…", "recycle.empty");
        Add("Refresh", "view.refresh");
        return menu;
    }
}
