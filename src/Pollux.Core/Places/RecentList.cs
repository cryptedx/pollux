using Pollux.Core.Navigation;

namespace Pollux.Core.Places;

public sealed class RecentEntry
{
    public string Path { get; set; } = "";
    public int Visits { get; set; }
    public DateTime LastVisit { get; set; }
}

/// <summary>Recent locations ranked by frecency: frequent and recent visits rank highest. Local only.</summary>
public sealed class RecentList
{
    public List<RecentEntry> Entries { get; set; } = [];

    public void Visit(string path, DateTime now, int maxItems, IEnumerable<string> exclude)
    {
        if (exclude.Any(x => WinPath.IsSameOrUnder(path, x)))
        {
            return;
        }

        var e = Entries.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (e is null)
        {
            e = new RecentEntry { Path = path };
            Entries.Add(e);
        }

        e.Visits++;
        e.LastVisit = now;

        if (Entries.Count > maxItems)
        {
            var keep = Ranked(now).Take(maxItems).ToHashSet();
            Entries.RemoveAll(x => !keep.Contains(x));
        }
    }

    public IEnumerable<RecentEntry> Ranked(DateTime now) => Entries.OrderByDescending(e => Score(e, now));

    public void Remove(string path) => Entries.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Clear() => Entries.Clear();

    private static double Score(RecentEntry e, DateTime now)
    {
        var hours = Math.Max(0, (now - e.LastVisit).TotalHours);
        var recency = hours switch { < 4 => 100, < 24 => 70, < 24 * 7 => 50, < 24 * 30 => 30, _ => 10 };
        return Math.Log2(1 + e.Visits) * recency;
    }
}
