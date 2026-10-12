using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.Session;

namespace Pollux.App.Views;

/// <summary>
/// Locations beyond folders (ADR 0010): zips, a server's shares and shell folders (This PC, Network, Control Panel,
/// Libraries, phones). Their crumbs, Up, opening items, the typed path and signing in to a server that refused access.
/// </summary>
public sealed partial class PaneView
{
    /// <summary>The shell's crumbs for the shell location shown last; only the shell can name a phone's folders.</summary>
    private (string Location, IReadOnlyList<LocationSegment> Segments)? _shellCrumbs;

    /// <summary>The Tab's glyph; null for a folder, which gets the drawn folder.</summary>
    private static string? TabGlyph(TabModel tab) => tab.Kind switch
    {
        LocationKind.Home => "⌂",
        LocationKind.RecycleBin => "♻",
        LocationKind.Zip => "🗜",
        LocationKind.Server => "🖧",
        LocationKind.Shell => tab.Location switch
        {
            WinPath.ThisPc => "🖥",
            WinPath.Network => "🖧",
            WinPath.ControlPanel => "⚙",
            WinPath.Libraries => "▤",
            _ => "◇",
        },
        _ => null,
    };

    private static string EmptyLabel(TabModel tab) => tab.Kind switch
    {
        LocationKind.RecycleBin => "Recycle Bin is empty",
        LocationKind.Server => "no shared folders",
        LocationKind.Shell when tab.Location == WinPath.Network => "no computers found (network discovery may be off in Windows)",
        LocationKind.Folder => "empty folder",
        _ => "empty",
    };

    /// <summary>What the path box starts with: the folder, a well-known name it understands, or the location as written.</summary>
    private static string PathBoxText(TabModel tab) => tab.Kind switch
    {
        LocationKind.Home => "",
        LocationKind.Folder => tab.Location,
        LocationKind.RecycleBin => tab.Title,
        _ when WinPath.WellKnown(tab.Title) is not null => tab.Title,
        _ => tab.Location,
    };

    private IReadOnlyList<LocationSegment> CrumbsFor(TabModel tab)
    {
        var location = tab.Location;
        if (tab.Kind != LocationKind.Shell || Locations.Title(location) is not null) return Locations.Segments(location);
        return _shellCrumbs is { } c && c.Location == location ? c.Segments : [new LocationSegment(tab.Title, location)];
    }

    private void RenderCrumbs()
    {
        CloseSuggestions();
        Crumbs.Children.Clear();
        PathBox.Visibility = Visibility.Collapsed;
        CrumbHost.Visibility = Visibility.Visible;
        var segments = CrumbsFor(Tab);
        var glyph = segments.Count == 1 && Tab.Kind != LocationKind.Folder ? TabGlyph(Tab) : null;
        for (var i = 0; i < segments.Count; i++)
        {
            var location = segments[i].Location;
            Crumbs.Children.Add(Crumb(glyph is null ? segments[i].Label : glyph + " " + segments[i].Label, location));
            var next = i + 1 < segments.Count ? segments[i + 1].Location : null;
            if (HasChildren(Locations.Classify(location))) Crumbs.Children.Add(Chevron(location, next));
            else if (next is not null) Crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Res("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
        }

        CrumbScroll.UpdateLayout();
        CrumbScroll.ChangeView(CrumbScroll.ScrollableWidth, null, null, true);
    }

    /// <summary>After listing a shell folder only the shell can name (a phone, a library): its crumbs and Tab title.</summary>
    private async Task NameShellLocationAsync(TabModel tab)
    {
        var location = tab.Location;
        if (tab.Kind != LocationKind.Shell || Locations.Title(location) is not null) return;
        if (_shellCrumbs?.Location == location) return;
        IReadOnlyList<LocationSegment> segments;
        try
        {
            segments = await VirtualLocations.Reader.SegmentsAsync(location);
        }
        catch (IOException)
        {
            return;
        }

        if (segments.Count == 0 || tab != Tab || tab.Location != location) return;
        _shellCrumbs = (location, segments);
        tab.SetDisplayName(location, segments[^1].Label);
        RenderTabs();
        if (PathBox.Visibility != Visibility.Visible) RenderCrumbs();
        _host.OnPaneStateChanged(this);
    }

    internal void Up()
    {
        if (Tab.IsHome) return;
        var location = Tab.Location;
        var child = Tab.Kind == LocationKind.Shell ? Tab.Title : WinPath.Name(location);
        if (Locations.Parent(location) is { } parent)
        {
            Navigate(parent, child);
            return;
        }

        if (Tab.Kind == LocationKind.Shell) _ = UpThroughShellAsync(location, child);
        else Navigate(WinPath.Home);
    }

    /// <summary>Up from a shell folder Pollux cannot name the parent of (a phone's folder): the shell's crumb before it.</summary>
    private async Task UpThroughShellAsync(string location, string child)
    {
        IReadOnlyList<LocationSegment> segments;
        try
        {
            segments = _shellCrumbs is { } c && c.Location == location ? c.Segments : await VirtualLocations.Reader.SegmentsAsync(location);
        }
        catch (IOException)
        {
            segments = [];
        }

        if (Tab.Location != location) return;
        Navigate(segments.Count >= 2 ? segments[^2].Location : WinPath.Home, child);
    }

    /// <summary>The path box's Enter: a well-known name, a shell location (a real folder when it has one), a server or a path.</summary>
    private async Task GoToTypedAsync(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0)
        {
            RenderCrumbs();
            FocusList();
            return;
        }

        var target = Locations.ResolveTyped(text, Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);
        if (Locations.Classify(target) == LocationKind.Shell && !target.StartsWith("pollux:", StringComparison.OrdinalIgnoreCase))
        {
            // shell:Downloads and the like are real folders: shown as such, with everything a folder offers.
            var real = await StaThread.Run(() => ShellNamespace.Instance.FileSystemPath(target));
            if (!string.IsNullOrEmpty(real)) target = WinPath.Normalize(real);
        }

        var exists = Locations.Classify(target) != LocationKind.Folder || await Task.Run(() => Directory.Exists(target) || File.Exists(target));
        if (exists)
        {
            Navigate(target);
            FocusList();
            return;
        }

        PathBox.Header = null;
        PathBox.Foreground = Res("PolluxBadBrush");
        ToolTipService.SetToolTip(PathBox, "Path not found: " + target);
    }

    internal void OpenSelected()
    {
        if (Tab.IsRecycleBin) return; // recycled entries are only restored or deleted, never opened
        var sel = SelectedItems;
        if (sel.Count == 1) Open(sel[0]);
        else foreach (var f in sel.Where(i => !i.IsDirectory)) _ = OpenItemAsync(f, browseZips: false);
    }

    /// <summary>Opens the selected files in their apps, a .zip included (Enter would enter it); folders are skipped.</summary>
    internal void OpenSelectedExternally()
    {
        if (Tab.IsRecycleBin) return;
        foreach (var f in SelectedItems.Where(i => !i.IsDirectory)) _ = OpenItemAsync(f, browseZips: false);
    }

    private void Open(FileItem item)
    {
        if (Tab.IsRecycleBin) return;
        _ = OpenItemAsync(item, browseZips: true);
    }

    /// <summary>Enter on any item: folders, zips and servers are entered, a file in a zip is extracted and opened, a shell item runs its default verb.</summary>
    private async Task OpenItemAsync(FileItem item, bool browseZips)
    {
        var tab = Tab;
        var (navigate, error) = await VirtualLocations.OpenAsync(_host.Hwnd, item.Entry, _host.ConfirmExtractLimit, browseZips);
        if (error is not null) _host.ShowToast(error, 6);
        else if (navigate is not null && tab == Tab) Navigate(navigate);
    }

    /// <summary>Shows "Sign in…" when a server or share refused access and Windows' credential prompt can fix that.</summary>
    private void OfferSignIn(TabModel tab, Exception error) =>
        SignInButton.Visibility = NetShares.NeedsSignIn(error) && Locations.ServerOfPath(tab.Location) is not null ? Visibility.Visible : Visibility.Collapsed;

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (Locations.ServerOfPath(Tab.Location) is not { } server) return;
        var hwnd = _host.Hwnd;
        var message = await Task.Run(() => NetShares.ConnectInteractive(hwnd, server));
        if (message is null) Refresh();
        else if (message.Length > 0) _host.ShowToast("Could not sign in: " + message, 6);
    }
}
