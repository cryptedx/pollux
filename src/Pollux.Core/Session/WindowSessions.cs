namespace Pollux.Core.Session;

/// <summary>The pure part of opening a window: which <see cref="WindowSession"/> it starts from.</summary>
public static class WindowSessions
{
    /// <param name="restore">The caller wants the saved layout (cold start) rather than a fresh window.</param>
    /// <param name="restoreWindows">The "restore windows" setting.</param>
    /// <param name="saved">Saved windows from the last run (a restored one is returned as-is, not copied).</param>
    /// <param name="openWindows">Windows already open; the next saved one is restored.</param>
    /// <param name="navigateTo">Folder, or file whose folder is shown; replaces a fresh window's tabs, is prepended to a restored one's.</param>
    public static WindowSession ForNewWindow(
        bool restore, bool restoreWindows, IReadOnlyList<WindowSession> saved, int openWindows,
        bool dualPaneOnStart, string? navigateTo, Func<string, bool> dirExists)
    {
        WindowSession session;
        var restored = false;
        if (restore && restoreWindows && saved.Count > openWindows)
        {
            session = saved[openWindows];
            restored = true;
        }
        else
        {
            session = new WindowSession { DualPane = dualPaneOnStart };
            if (saved.Count > 0)
            {
                var last = saved[^1];
                session.Width = last.Width;
                session.Height = last.Height;
            }
        }

        if (navigateTo is not null)
        {
            var dir = dirExists(navigateTo) ? navigateTo : Path.GetDirectoryName(navigateTo)!;
            var tabs = session.Panes[session.ActivePane].Tabs;
            // A fresh window opened for a folder shows just that folder; a restored one keeps its tabs.
            if (!restored) tabs.Clear();
            tabs.Insert(0, new TabSession { Location = dir });
            session.Panes[session.ActivePane].ActiveTab = 0;
        }

        return session;
    }
}
