using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.ShellNew;

namespace Pollux.App.Views;

/// <summary>"New from Template": files from the registered ShellNew types, created in the active pane's folder.</summary>
internal sealed partial class MainWindow
{
    private List<PaletteItem> _templateItems = [];

    private async Task ShowTemplatePaletteAsync()
    {
        var entries = await _app.Services.ShellNew.EntriesAsync();
        if (entries.Count == 0)
        {
            ShowToast("No file templates registered");
            return;
        }

        _templateItems = [.. entries.Select(e => new PaletteItem(e.DisplayName, e.Extension, "", () => NewFromTemplate(e)))];
        ShowPalette(PaletteMode.Templates);
    }

    public void NewFromTemplate(ShellNewEntry entry) => _ = NewFromTemplateAsync(entry);

    private async Task NewFromTemplateAsync(ShellNewEntry entry)
    {
        if (!Active.Tab.Access.CanModify)
        {
            ShowToast("Read-only location");
            return;
        }

        if (Active.Folder is not { } dir) return;
        string path;
        try
        {
            path = await _app.Services.ShellNew.CreateAsync(dir, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message);
            return;
        }

        _app.Undo.Record(Core.Operations.UndoEntry.NewFile(path, FileUndo.Target(Hwnd)));
        var name = Path.GetFileName(path);
        Active.SelectByName(name);

        var renamed = await PromptAsync("Rename", name, selectStem: true);
        if (string.IsNullOrWhiteSpace(renamed) || renamed == name) return;
        if (renamed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowToast("Invalid file name");
            return;
        }

        if (Shell.Rename(Hwnd, path, renamed))
        {
            _app.Undo.Record(Core.Operations.UndoEntry.Rename(path, Path.Combine(dir, renamed)));
            Active.SelectByName(renamed);
        }
    }
}
