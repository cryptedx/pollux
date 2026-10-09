using Microsoft.UI.Xaml.Controls;
using Pollux.App.ViewModels;
using Pollux.Core.Copilot;

namespace Pollux.App.Views;

/// <summary>Pollux's compact context menus. "More options" falls back to the shell for everything else.</summary>
internal static class ContextMenus
{
    public static MenuFlyout ForSelection(IPaneHost host, IReadOnlyList<FileItem> selection, string? folder)
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
            Add("Copy path", "file.copyPath");
            Add("Copy path (quoted)", "file.copyPathQuoted");
            Add("Copy path (forward slashes)", "file.copyPathForward");
            Add("Copy name", "file.copyName");
            Sep();
            if (selection.Count == 1) Add("Rename", "file.rename");
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
}
