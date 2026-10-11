using Microsoft.UI.Xaml.Controls;
using Pollux.App.ViewModels;
using Pollux.Core.Copilot;

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
            Add("New folder", "file.newFolder");
            Add("New text file", "file.newFile");
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
