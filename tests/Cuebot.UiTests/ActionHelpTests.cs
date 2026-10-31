using System.Text;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;

namespace Cuebot.UiTests;

/// <summary>
/// Every trigger and sub-action the real composition root loads has plain-language help (src/Cuebot.Core/Help), and
/// the documentation site's reference pages (website/docs/reference) match it. Set CUEBOT_WRITE_REFERENCE=1 to
/// rewrite those pages after changing help, a type or a setting.
/// </summary>
public sealed class ActionHelpTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-help-").FullName;
    private readonly AppHost _host;

    public ActionHelpTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private Registry Registry => _host.Get<Registry>();

    [Fact]
    public void Every_trigger_and_sub_action_has_a_description_and_help_for_each_setting()
    {
        var problems = new List<string>();
        foreach (var t in Registry.Triggers) Check("trigger", t.TypeId, ActionHelp.Trigger(t.TypeId), t.Parameters, problems);
        foreach (var h in Registry.SubActions) Check("sub-action", h.TypeId, ActionHelp.SubAction(h.TypeId), h.Parameters, problems);
        Assert.True(problems.Count == 0, $"{problems.Count} help problems (see docs/WRITING_HELP.md):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Help_files_name_only_types_and_settings_that_exist()
    {
        var problems = new List<string>();
        foreach (var id in ActionHelp.TriggerIds)
        {
            if (Registry.GetTrigger(id) is { } t) Unknown("trigger", id, ActionHelp.Trigger(id)!, t.Parameters, problems);
            else problems.Add($"trigger {id}: no such trigger");
        }
        foreach (var id in ActionHelp.SubActionIds)
        {
            if (Registry.GetSubAction(id) is { } h) Unknown("sub-action", id, ActionHelp.SubAction(id)!, h.Parameters, problems);
            else problems.Add($"sub-action {id}: no such sub-action");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_trigger_editor_shows_the_description_variables_and_field_help()
    {
        var type = Registry.GetTrigger("twitch.cheer")!;
        var editor = new Cuebot.App.ViewModels.Actions.TriggerItemViewModel(new TriggerDefinition { Type = type.TypeId }, type, _ => { });
        var help = ActionHelp.Trigger(type.TypeId)!;

        Assert.Equal(help.Description, editor.Description);
        Assert.True(editor.HasVariables);
        Assert.Contains(editor.Variables, v => v.StartsWith("%bits% — ", StringComparison.Ordinal));
        Assert.Equal(help.Fields["minBits"], editor.Parameters.Single(p => p.Descriptor.Key == "minBits").Help);
    }

    [Fact]
    public void Reference_pages_match_the_help()
    {
        var root = Path.Combine(RepoRoot(), "website", "docs", "reference");
        var pages = ReferencePages.Build(Registry);
        if (Environment.GetEnvironmentVariable("CUEBOT_WRITE_REFERENCE") is { Length: > 0 })
        {
            foreach (var dir in new[] { "triggers", "sub-actions" })
            {
                var path = Path.Combine(root, dir);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            foreach (var (file, text) in pages)
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
        }
        var stale = pages.Where(p => !File.Exists(Path.Combine(root, p.Key)) || File.ReadAllText(Path.Combine(root, p.Key)) != p.Value)
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var extra = new[] { "triggers", "sub-actions" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !pages.ContainsKey(f)).ToArray();
        Assert.True(stale.Length == 0 && extra.Length == 0,
            "Reference pages are out of date; run the tests with CUEBOT_WRITE_REFERENCE=1 to rewrite them.\n" +
            string.Join("\n", stale.Select(s => "stale: " + s).Concat(extra.Select(e => "extra: " + e))));
    }

    private static void Check(string kind, string id, HelpEntry? entry, IReadOnlyList<ParameterDescriptor> parameters, List<string> problems)
    {
        if (entry is not { Description.Length: > 0 }) problems.Add($"{kind} {id}: no description");
        foreach (var d in parameters)
        {
            if (string.IsNullOrWhiteSpace(ActionHelp.Field(entry, d))) problems.Add($"{kind} {id}: setting '{d.Key}' has no help");
        }
    }

    private static void Unknown(string kind, string id, HelpEntry entry, IReadOnlyList<ParameterDescriptor> parameters, List<string> problems)
    {
        var keys = parameters.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in entry.Fields.Keys.Where(k => !keys.Contains(k))) problems.Add($"{kind} {id}: no setting '{key}'");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Cuebot.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Cuebot.slnx not found above the test folder");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>The documentation site's trigger and sub-action reference, one Markdown page per category.</summary>
internal static class ReferencePages
{
    public static SortedDictionary<string, string> Build(Registry registry)
    {
        var pages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var triggers = registry.Triggers.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var subActions = registry.SubActions.GroupBy(h => h.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();

        pages["triggers/index.md"] = Index("Triggers",
            "A **trigger** is the \"when\" of an action: *when* someone follows, *when* a scene changes, *when* you press a key. " +
            "Add one or more triggers to an action and it runs every time one of them happens.",
            triggers.Select(g => (g.Key, g.Count())));
        foreach (var g in triggers)
        {
            pages[$"triggers/{Slug(g.Key)}.md"] = Category($"{g.Key} triggers", g.Select(t =>
                (t.DisplayName, t.TypeId, ActionHelp.Trigger(t.TypeId), t.Parameters, (IReadOnlyList<string>)[])), "Variables you get");
        }

        pages["sub-actions/index.md"] = Index("Sub-actions",
            "A **sub-action** is one step of an action: the \"then do this\" part. An action runs its sub-actions from top to " +
            "bottom, like a recipe: send a chat message, wait two seconds, switch the OBS scene.",
            subActions.Select(g => (g.Key, g.Count())));
        foreach (var g in subActions)
        {
            pages[$"sub-actions/{Slug(g.Key)}.md"] = Category($"{g.Key} sub-actions", g.Select(h =>
                (h.DisplayName, h.TypeId, ActionHelp.SubAction(h.TypeId), h.Parameters, h is IDynamicBranchesProvider ? [] : h.Branches)),
                "Variables it adds");
        }
        return pages;
    }

    private static string Index(string title, string intro, IEnumerable<(string Category, int Count)> categories)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- Generated by ActionHelpTests from src/Cuebot.Core/Help. Do not edit by hand. -->\n");
        sb.Append($"# {title}\n\n{intro}\n\n| Area | How many |\n| --- | --- |\n");
        foreach (var (category, count) in categories) sb.Append($"| [{Cell(category)}]({Slug(category)}.md) | {count} |\n");
        return sb.ToString();
    }

    private static string Category(string title,
        IEnumerable<(string Name, string TypeId, HelpEntry? Help, IReadOnlyList<ParameterDescriptor> Parameters, IReadOnlyList<string> Branches)> items,
        string variablesHeading)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- Generated by ActionHelpTests from src/Cuebot.Core/Help. Do not edit by hand. -->\n");
        sb.Append($"# {title}\n");
        foreach (var item in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"\n## {item.Name}\n\n");
            if (item.Help is { Description.Length: > 0 } help) sb.Append(help.Description).Append("\n\n");
            if (item.Parameters.Count > 0)
            {
                sb.Append("| Setting | What to put in |\n| --- | --- |\n");
                foreach (var d in item.Parameters)
                {
                    var text = ActionHelp.Field(item.Help, d) ?? "";
                    if (d.Kind == ParameterKind.Choice && d.Choices.Count > 0)
                        text += (text.Length > 0 ? " " : "") + "Options: " + string.Join(", ", d.Choices) + ".";
                    if (d.DefaultValue.Length > 0 && d.Kind != ParameterKind.Code) text += $" Starts as: `{d.DefaultValue}`.";
                    sb.Append($"| **{Cell(d.Label)}** | {Cell(text.Trim())} |\n");
                }
                sb.Append('\n');
            }
            if (item.Branches.Count > 0) sb.Append("Paths it can take: ").Append(string.Join(", ", item.Branches.Select(b => $"**{b}**"))).Append(".\n\n");
            if (item.Help is { Variables.Count: > 0 } withVariables)
            {
                sb.Append($"**{variablesHeading}**\n\n| Variable | What it holds |\n| --- | --- |\n");
                foreach (var (name, meaning) in withVariables.Variables) sb.Append($"| `%{name}%` | {Cell(meaning)} |\n");
                sb.Append('\n');
            }
            sb.Append($"<small>Type id: `{item.TypeId}`</small>\n");
        }
        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

    public static string Slug(string category)
    {
        var sb = new StringBuilder();
        foreach (var c in category.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
