using Pollux.Core.Copilot.Guards;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>A permission rule of the CLI, <c>kind(argument)</c>: e.g. shell(format), read(C:\Users\me\.ssh), write(*.key).</summary>
public sealed record CliRule(string Kind, string Argument);

/// <summary>
/// The Guard Rules of one Chat (spec "Guard Rules"): the engine with the user's extensions from settings, judged against
/// the Chat's current Working Folder and Tainted state. Holds in every Permission Mode.
/// </summary>
/// <param name="settings">Extensions from <c>copilot.guardRules</c>; null uses the defaults only.</param>
/// <param name="workingFolder">The Chat's Working Folder now (it can change while the Chat is open); null or empty: none, every write asks.</param>
/// <param name="tainted">Whether the Chat is Tainted now (ticket 13); null: never.</param>
/// <param name="environment">Resolves %VARS%, USERPROFILE etc.; null uses the process environment.</param>
/// <param name="markTainted">Makes the Chat Tainted, with the reason; called when a tool call that reads untrusted content goes ahead.</param>
/// <param name="folders">Lists the files below a searched folder (protected files there make the search ask); null: the real file system.</param>
public sealed class ChatGuard(
    GuardRuleSettings? settings = null,
    Func<string?>? workingFolder = null,
    Func<bool>? tainted = null,
    Func<string, string?>? environment = null,
    Action<string>? markTainted = null,
    IFolderFiles? folders = null)
{
    private readonly GuardRules _rules = new(settings, folders);
    private readonly GuardRuleSettings _settings = settings ?? new();
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>The Guard Rules' verdict on a tool request in the Chat's current state.</summary>
    public GuardVerdict Evaluate(ToolRequest request) => _rules.Evaluate(request, Context());

    /// <summary>
    /// A tool call goes ahead (not denied by the Guard Rules or the user): when it brings untrusted content into the
    /// Chat (Downloads, Inbox, Temp, archives, web), the Chat becomes a Tainted Chat.
    /// </summary>
    public void Proceeds(ToolRequest request)
    {
        if (markTainted is null || (tainted?.Invoke() ?? false)) return;
        if (_rules.Taint(request, Context()) is { } reason) markTainted(reason);
    }

    private GuardContext Context() => new(workingFolder?.Invoke() ?? "", tainted?.Invoke() ?? false, _environment);

    /// <summary>
    /// The protected secrets as the CLI's own read and write deny rules (a further layer that does not depend on the
    /// pre-tool-use hook, which matters under Allow All where the CLI asks nothing): every protected location as an
    /// absolute path, itself and everything below it (<c>X</c> and <c>X\**</c>), and every name pattern (".env*", "*.key")
    /// as given, which the CLI matches against the trailing path components. A location whose %VARS% or ~ cannot be
    /// resolved is left to the hook. Shell commands are not covered by these rules (the CLI's own rule syntax); the
    /// hook and the shell deny rules are.
    /// </summary>
    public IReadOnlyList<CliRule> CliDeniedPaths
    {
        get
        {
            var context = Context();
            var arguments = new List<string>();
            foreach (var pattern in ProtectedPaths.Defaults.Concat(_settings.ProtectedPaths).Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (!pattern.Contains('\\', StringComparison.Ordinal) && !pattern.Contains('/', StringComparison.Ordinal))
                {
                    arguments.Add(pattern);
                    continue;
                }

                var location = GuardPaths.Resolve(pattern, context);
                if (location.Contains('%', StringComparison.Ordinal) || location.Contains('~', StringComparison.Ordinal) || !IsAbsolute(location)) continue;
                arguments.Add(location);
                arguments.Add(location.TrimEnd('\\') + @"\**");
            }

            return [.. arguments.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(a => new[] { new CliRule("read", a), new CliRule("write", a) })];
        }
    }

    private static bool IsAbsolute(string path) => (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\') || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Shell command names given to the CLI as per-Chat deny rules (first layer; documented to win even over Allow All):
    /// the built-in blocked commands plus the blocked commands from settings that are a bare command name. Entries
    /// with arguments ("git push --tags") cannot be expressed as a CLI rule without blocking the whole command.
    /// </summary>
    public IReadOnlyList<string> CliDeniedCommands =>
    [
        .. GuardRules.BlockedCommandNames,
        .. _settings.BlockedCommands
            .Select(e => e.Trim())
            .Where(e => e.Length > 0 && !e.Any(char.IsWhiteSpace))
            .Select(e => e.ToLowerInvariant())
            .Where(e => !GuardRules.BlockedCommandNames.Contains(e)),
    ];
}
