using System.Globalization;
using Pollux.Core.Settings;

namespace Pollux.Core.Copilot;

/// <summary>One selection action: a title, the prompt it sends and the command that runs it (context menu and palette).</summary>
public sealed record SelectionAction(string CommandId, string Title, string Prompt);

/// <summary>The built-in selection actions plus the user's prompt templates from settings.json.</summary>
public static class SelectionActions
{
    public const string ActionPrefix = "copilot.action.";
    public const string TemplatePrefix = "copilot.template.";

    private static readonly SelectionAction[] BuiltIn =
    [
        new(ActionPrefix + "explain", "Explain", "Explain what the selected files and folders are and what they contain."),
        new(ActionPrefix + "summarize", "Summarize", "Summarize the content of the selected files and folders briefly."),
        new(ActionPrefix + "renameByContent", "Rename by content",
            "Read the content of the selected files and propose a descriptive name for each. List the proposed renames and wait for my confirmation before renaming anything."),
    ];

    public static IReadOnlyList<SelectionAction> For(CopilotSettings settings)
    {
        var list = new List<SelectionAction>(BuiltIn);
        var templates = settings.PromptTemplates ?? [];
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            if (t is null || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Prompt)) continue;
            list.Add(new(TemplatePrefix + i.ToString(CultureInfo.InvariantCulture), t.Name.Trim(), t.Prompt));
        }

        return list;
    }

    public static SelectionAction? Find(CopilotSettings settings, string commandId) =>
        For(settings).FirstOrDefault(a => a.CommandId == commandId);

    public static bool IsSelectionAction(string commandId) =>
        commandId.StartsWith(ActionPrefix, StringComparison.Ordinal) || commandId.StartsWith(TemplatePrefix, StringComparison.Ordinal);
}
