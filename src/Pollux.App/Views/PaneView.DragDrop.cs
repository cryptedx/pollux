using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Navigation;
using Pollux.Core.Operations;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace Pollux.App.Views;

/// <summary>
/// Drag and drop like Explorer: a folder row under the pointer, a path-bar segment or a Tab is the drop target, otherwise
/// the shown folder. <see cref="DropDecision"/> picks copy, move or shortcut from the keys and volumes; a right-button drag asks.
/// </summary>
public sealed partial class PaneView
{
    private const string SourcePathsKey = "PolluxSourcePaths";
    private const string CopyOutKey = "PolluxCopyOut";
    private DataPackageView? _dragView;
    private IReadOnlyList<string> _dragSources = [];
    private DropKeys _dragKeys;
    private ListViewItem? _dropRow;
    private DispatcherQueueTimer? _dragKeyTimer;
    private DropKeys _polledKeys;
    private Native.POINT? _nudgedFrom;

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileItem>().Select(i => i.Path).ToList();
        CrashLog.Trace($"drag start pane {Index}: {paths.Count} items");
        // Recycled entries leave the bin only through Restore.
        if (paths.Count == 0 || Tab.IsRecycleBin) { e.Cancel = true; return; }
        if (IsCopyOutOnly(paths))
        {
            StartCopyOutDrag(e, paths);
            return;
        }

        // A drive or share (This PC, a server) is only ever linked, in Pollux and in Explorer.
        e.Data.RequestedOperation = paths.Any(DropDecision.IsVolumeRoot)
            ? DataPackageOperation.Link
            : DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
        // Drop targets in Pollux decide during the drag without asking the data provider.
        e.Data.Properties.Add(SourcePathsKey, string.Join('\n', paths));
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await StorageItemsFor(paths)); }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>
    /// Items without a file of their own (entries of a zip, a phone's files): only copied out. Pollux's own drop targets copy
    /// them out directly; another app gets them extracted or copied to the temp folder when it asks at drop time.
    /// </summary>
    private void StartCopyOutDrag(DragItemsStartingEventArgs e, List<string> paths)
    {
        if (!Tab.Access.CanCopyOut || paths.Any(p => Locations.ServerOf(p) is not null)) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.Add(SourcePathsKey, string.Join('\n', paths));
        e.Data.Properties.Add(CopyOutKey, true);
        var owner = _host.Hwnd;
        var confirm = _host.ConfirmExtractLimit;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await StorageItemsFor(await VirtualLocations.MaterializeAsync(owner, paths, confirm))); }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>Whether these items of the shown location have no file of their own: anything in a zip, shell items without a path.</summary>
    internal bool IsCopyOutOnly(IReadOnlyList<string> paths) =>
        Tab.Kind == LocationKind.Zip || paths.Any(p => Locations.IsShell(p) || Locations.ServerOf(p) is not null);

    internal static async Task<List<IStorageItem>> StorageItemsFor(IEnumerable<string> paths)
    {
        var list = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                list.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip items that vanished or are not accessible.
            }
        }

        return list;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var (row, target) = ListDropTarget(sender, e.OriginalSource);
        HighlightDropRow(row);
        AcceptDrop(e, target);
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        HighlightDropRow(null);
        StopDragKeyWatch();
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        HighlightDropRow(null);
        StopDragKeyWatch();
        var (_, target) = ListDropTarget(sender, e.OriginalSource);
        DropOn(e, target, sender as UIElement);
    }

    /// <summary>The folder row under the pointer and its path, or no row and the shown folder.</summary>
    private (ListViewItem? Row, string? Target) ListDropTarget(object sender, object source)
    {
        if (Tab.IsRecycleBin) return (null, null);
        for (var d = source as DependencyObject; d is not null && !ReferenceEquals(d, sender); d = VisualTreeHelper.GetParent(d))
        {
            if (d is not ListViewItem row) continue;
            // A folder row takes drops when it is a real folder (a drive in This PC too), never a folder in a zip or a shell folder.
            return (sender as ListViewBase)?.ItemFromContainer(row) is FileItem { IsDirectory: true } item && Locations.Classify(item.Path) == LocationKind.Folder
                ? (row, item.Path)
                : (null, Folder);
        }

        return (null, Folder);
    }

    private void HighlightDropRow(ListViewItem? row)
    {
        if (row == _dropRow) return;
        if (_dropRow is { } old)
        {
            old.ClearValue(BackgroundProperty);
            old.ClearValue(BorderBrushProperty);
            old.ClearValue(BorderThicknessProperty);
        }

        _dropRow = row;
        if (row is null) return;
        row.Background = Res("PolluxAccentSoftBrush");
        row.BorderBrush = Res("PolluxAccentBrush");
        row.BorderThickness = new Thickness(1);
    }

    /// <summary>Makes a path-bar segment or a Tab accept file drops into <paramref name="target"/>.</summary>
    private void AcceptFileDrops(UIElement element, string? target)
    {
        if (target is null || target == WinPath.Home || target == WinPath.RecycleBin) return;
        element.AllowDrop = true;
        element.DragOver += (_, e) =>
        {
            if (_draggedTab is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            AcceptDrop(e, target);
            e.Handled = true;
        };
        element.DragLeave += (_, _) => StopDragKeyWatch();
        element.Drop += (_, e) =>
        {
            StopDragKeyWatch();
            if (_draggedTab is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.Handled = true;
            DropOn(e, target, element);
        };
    }

    /// <summary>Sets the operation and caption for a drag over <paramref name="target"/>; null refuses it.</summary>
    private void AcceptDrop(DragEventArgs e, string? target)
    {
        _dragKeys = Keys(e.Modifiers);
        StartDragKeyWatch();
        if (target is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (KnownSources(e.DataView) is { } sources)
        {
            Apply(e, sources, target);
            return;
        }

        // An outside source: read its paths once per drag (the deferral holds the drag until they are known).
        _ = LoadAndApplyAsync(e, target);
    }

    private async Task LoadAndApplyAsync(DragEventArgs e, string target)
    {
        var deferral = e.GetDeferral();
        try { Apply(e, await LoadSourcesAsync(e.DataView), target); }
        finally { deferral.Complete(); }
    }

    private void Apply(DragEventArgs e, IReadOnlyList<string> sources, string target)
    {
        var allowed = Allowed(e.DataView);
        var plan = DropDecision.Decide(_dragKeys, sources, target, allowed, CopiesOut(e.DataView));
        e.AcceptedOperation = (DataPackageOperation)DropDecision.Reported(plan.Operation, allowed);
        e.DragUIOverride.Caption = plan.Caption(WinPath.Name(target));
        e.DragUIOverride.IsCaptionVisible = plan.Operation != DropOperation.None;
    }

    private async void DropOn(DragEventArgs e, string? target, UIElement? anchor)
    {
        CrashLog.Trace($"drop pane {Index}: op={e.AcceptedOperation}");
        if (target is null) return;
        var allowed = Allowed(e.DataView);
        var position = anchor is null ? default : e.GetPosition(anchor);
        var copyOut = CopiesOut(e.DataView);
        DropPlan plan;
        IReadOnlyList<string> sources;
        var deferral = e.GetDeferral();
        try
        {
            sources = KnownSources(e.DataView) ?? await LoadSourcesAsync(e.DataView);
            // The keys of the last DragOver: a right-button drag has released its button by now.
            plan = DropDecision.Decide(_dragKeys, sources, target, allowed, copyOut);
            // Pollux moves the items itself; reporting Copy keeps the source from deleting them as an unoptimized move.
            e.AcceptedOperation = plan.Operation == DropOperation.None ? DataPackageOperation.None : DataPackageOperation.Copy;
        }
        finally
        {
            deferral.Complete();
            _dragView = null;
            _dragSources = [];
        }

        if (plan.Operation == DropOperation.None) return;
        _host.OnPaneActivated(this);
        if (!plan.Ask) RunDrop(plan.Operation, sources, target, copyOut);
        else if (anchor is not null) DropMenu(plan.Operation, allowed, sources, target, copyOut).ShowAt(anchor, position);
    }

    private MenuFlyout DropMenu(DropOperation preferred, DropOperation allowed, IReadOnlyList<string> sources, string target, bool copyOut)
    {
        var menu = new MenuFlyout();
        foreach (var op in DropDecision.Choices(allowed, copyOut, linkOnly: sources.Any(DropDecision.IsVolumeRoot)))
        {
            var item = new MenuFlyoutItem
            {
                Text = op switch { DropOperation.Copy => "Copy here", DropOperation.Move => "Move here", _ => "Create shortcuts here" },
                FontWeight = op == preferred ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => RunDrop(op, sources, target, copyOut);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Cancel" });
        return menu;
    }

    private void RunDrop(DropOperation op, IReadOnlyList<string> sources, string target, bool copyOut)
    {
        if (copyOut) _host.CopyOut(sources, target);
        else if (op == DropOperation.Link) _host.CreateShortcuts(sources, target);
        else _host.Transfer(op == DropOperation.Move, sources, target);
    }

    /// <summary>The dragged paths when Pollux started the drag or they were read earlier in this drag; null when they must be read.</summary>
    private IReadOnlyList<string>? KnownSources(DataPackageView view)
    {
        if (view.Properties.TryGetValue(SourcePathsKey, out var value) && value is string joined) return joined.Split('\n');
        return ReferenceEquals(view, _dragView) ? _dragSources : null;
    }

    private async Task<IReadOnlyList<string>> LoadSourcesAsync(DataPackageView view)
    {
        try
        {
            var items = await view.GetStorageItemsAsync();
            _dragSources = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _dragSources = [];
        }

        _dragView = view;
        return _dragSources;
    }

    /// <summary>The drag carries items of a zip or shell folder that Pollux started (<see cref="StartCopyOutDrag"/>).</summary>
    private static bool CopiesOut(DataPackageView view) => view.Properties.ContainsKey(CopyOutKey);

    /// <summary>What the source allows; a source that names nothing is treated as copy-only.</summary>
    private static DropOperation Allowed(DataPackageView view) =>
        view.RequestedOperation == DataPackageOperation.None ? DropOperation.Copy : (DropOperation)view.RequestedOperation;

    private static DropKeys Keys(DragDropModifiers m) =>
        (m.HasFlag(DragDropModifiers.Shift) ? DropKeys.Shift : 0)
        | (m.HasFlag(DragDropModifiers.Control) ? DropKeys.Control : 0)
        | (m.HasFlag(DragDropModifiers.Alt) ? DropKeys.Alt : 0)
        | (m.HasFlag(DragDropModifiers.RightButton) ? DropKeys.RightButton : 0);

    // ---------------- modifier keys while the pointer rests ----------------

    // WinUI raises DragOver only when the pointer moves, so Shift, Ctrl or Alt pressed over a target would not change the
    // operation and caption until the next move. While a drag is over this Pane the keys are polled; when they change,
    // the cursor is nudged by a pixel and put back, and each step raises DragOver with the new keys.

    private void StartDragKeyWatch()
    {
        if (_dragKeyTimer is null)
        {
            _dragKeyTimer = DispatcherQueue.CreateTimer();
            _dragKeyTimer.Interval = TimeSpan.FromMilliseconds(30);
            _dragKeyTimer.Tick += (_, _) => DragKeyTick();
        }

        if (_dragKeyTimer.IsRunning) return;
        _polledKeys = _dragKeys & ~DropKeys.RightButton;
        _dragKeyTimer.Start();
    }

    private void StopDragKeyWatch()
    {
        _dragKeyTimer?.Stop();
        if (_nudgedFrom is { } home) Native.SetCursorPos(home.X, home.Y);
        _nudgedFrom = null;
    }

    private void DragKeyTick()
    {
        // The drag ended without a Drop or DragLeave here (dropped elsewhere, cancelled with Esc).
        if (!IsDown(VK_LBUTTON) && !IsDown(VK_RBUTTON)) { StopDragKeyWatch(); return; }
        if (_nudgedFrom is { } home)
        {
            Native.SetCursorPos(home.X, home.Y);
            _nudgedFrom = null;
            return;
        }

        var keys = (IsDown(VK_SHIFT) ? DropKeys.Shift : 0) | (IsDown(VK_CONTROL) ? DropKeys.Control : 0) | (IsDown(VK_MENU) ? DropKeys.Alt : 0);
        if (keys == _polledKeys) return;
        _polledKeys = keys;
        if (keys == (_dragKeys & ~DropKeys.RightButton) || !Native.GetCursorPos(out var at)) return;
        Native.SetCursorPos(at.X + 1, at.Y);
        // At the right edge of the screen the cursor cannot move right.
        if (Native.GetCursorPos(out var moved) && moved.X == at.X) Native.SetCursorPos(at.X - 1, at.Y);
        _nudgedFrom = at;
    }

    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private static bool IsDown(int vk) => Native.GetAsyncKeyState(vk) < 0;

    // ---------------- middle-click ----------------

    /// <summary>The folder a middle-click opens in a background Tab: a folder row or a path-bar segment; null otherwise.</summary>
    private string? MiddleClickFolder(DependencyObject? source)
    {
        for (var d = source; d is not null && d != this; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListViewItem row) return List.ItemFromContainer(row) is FileItem { IsDirectory: true } item && !Tab.IsRecycleBin ? item.Path : null;
            if (d is Button { Tag: string target } b && b.Parent == Crumbs) return target;
        }

        return null;
    }
}
