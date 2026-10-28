using System.Reflection;
using Cuebot.Core.Transfer.StreamerBot;
using Cuebot.Scripting.Compat;
using Streamer.bot.Common.Events;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Every method, enum value and type name in Streamer.bot's C# reference exists in the CPH layer. The name lists in
/// Data/ were generated from the public docs (names only).
/// </summary>
public sealed class StreamerBotApiCoverageTests
{
    private static IReadOnlyList<string> Names(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", file))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

    [Fact]
    public void Every_documented_CPH_method_exists()
    {
        var methods = typeof(CphProxy).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var documented = Names("cph-methods.txt");

        Assert.Equal(202, documented.Count);
        Assert.Equal(Array.Empty<string>(), documented.Where(n => !methods.Contains(n)).ToArray());
    }

    [Fact]
    public void Every_CPH_method_named_on_subaction_pages_and_in_changelogs_exists()
    {
        var methods = typeof(CphProxy).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var documented = Names("cph-methods-more.txt");

        Assert.Equal(284, documented.Count);
        Assert.Equal(Array.Empty<string>(), documented.Where(n => !methods.Contains(n)).ToArray());
    }

    /// <summary>
    /// Every published signature compiles as a call: once with every argument and once with only the required ones,
    /// assigning the result to the documented return type. This catches missing overloads and incompatible types,
    /// which the name lists do not.
    /// </summary>
    [Fact]
    public void Every_published_CPH_signature_compiles_as_a_call()
    {
        var signatures = Names("cph-signatures.txt");
        Assert.Equal(582, signatures.Count);

        var calls = signatures.Select(Call).ToArray();
        using var host = new CphInlineHost();
        if (Compile(host, calls) is null) return;

        // Something failed: compile one signature at a time to name them.
        var failures = signatures.Select((s, i) => Compile(host, [calls[i]]) is { } error ? s + "\n    " + error : null)
            .OfType<string>().ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} signatures do not compile:\n" + string.Join("\n", failures));
    }

    /// <summary>Compiles the calls; null when they compile, else the compiler's message.</summary>
    private static string? Compile(CphInlineHost host, IReadOnlyList<string> calls)
    {
        var code = "public class CPHInline\n{\n    public bool Execute() => true;\n"
                   + string.Concat(calls.Select((c, i) => $"    public void M{i}()\n    {{\n{c}    }}\n"))
                   + "}\n";
        try
        {
            host.Get(code);
            return null;
        }
        catch (Cuebot.Core.Scripting.ScriptException ex)
        {
            return ex.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>The statements calling one signature ("bool Name(string a, int b = 0);"); a generic T becomes string.</summary>
    private static string Call(string signature)
    {
        var open = signature.IndexOf('(');
        var head = signature[..open].Trim();
        var space = LastTopLevel(head, ' ');
        var returns = Generic(head[..space].Trim());
        var name = head[(space + 1)..];
        if (name.EndsWith("<T>", StringComparison.Ordinal)) name = name[..^3] + "<string>";

        var parameters = Split(signature[(open + 1)..signature.LastIndexOf(')')]);
        var all = parameters.Select(Argument).ToList();
        var required = parameters.TakeWhile(p => !p.Contains('=')).Select(Argument).ToList();

        string Statement(List<string> args) => returns == "void"
            ? $"        CPH.{name}({string.Join(", ", args)});\n"
            : $"        {{ {returns} r = CPH.{name}({string.Join(", ", args)}); }}\n";

        return Statement(all) + (required.Count < all.Count ? Statement(required) : "");
    }

    private static string Argument(string parameter)
    {
        var text = parameter.Split('=')[0].Trim();
        var isOut = text.StartsWith("out ", StringComparison.Ordinal);
        if (isOut) text = text[4..];
        var type = Generic(text[..LastTopLevel(text, ' ')].Trim());
        return isOut ? $"out {type} _" : $"default({type})";
    }

    private static string Generic(string type) =>
        type == "T" ? "string" : type.Replace("<T>", "<string>", StringComparison.Ordinal);

    private static int LastTopLevel(string text, char c)
    {
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '>') depth++;
            else if (text[i] == '<') depth--;
            else if (text[i] == c && depth == 0) return i;
        }
        return -1;
    }

    private static List<string> Split(string parameters)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] == '<') depth++;
            else if (parameters[i] == '>') depth--;
            else if (parameters[i] == ',' && depth == 0)
            {
                result.Add(parameters[start..i]);
                start = i + 1;
            }
        }
        if (parameters[start..].Trim().Length > 0) result.Add(parameters[start..]);
        return result;
    }

    [Fact]
    public void Every_documented_EventType_and_EventSource_value_exists()
    {
        var types = Names("event-types.txt");
        var sources = Names("event-sources.txt");

        Assert.Equal(508, types.Count);
        Assert.Equal(46, sources.Count);
        Assert.Equal(Array.Empty<string>(), types.Where(n => !Enum.IsDefined(typeof(EventType), n)).ToArray());
        Assert.Equal(Array.Empty<string>(), sources.Where(n => !Enum.IsDefined(typeof(EventSource), n)).ToArray());
        // Names earlier Cuebot versions used still parse, as aliases of the documented values.
        Assert.Equal(EventType.GlobalVariableCreated, Enum.Parse<EventType>("MiscGlobalVariableCreated"));
        Assert.Equal(EventType.SpeechToTextLog, Enum.Parse<EventType>("SpeechToTextDictation"));
    }

    [Fact]
    public void EventType_values_are_streamerbots_trigger_codes()
    {
        // __source carries the code; Streamer.bot C# compares it with EventType values.
        Assert.Equal(Array.Empty<int>(), StreamerBotTriggerCatalog.Paths.Keys.Where(c => !Enum.IsDefined(typeof(EventType), c)).ToArray());
        Assert.Equal((101, 706, 18001, 18002, 4016), ((int)EventType.TwitchFollow, (int)EventType.StreamerbotStarted, (int)EventType.CustomEvent,
            (int)EventType.CustomCodeEvent, (int)EventType.YouTubeFirstWords));
        var values = Enum.GetNames<EventType>().Where(n => !n.StartsWith("Misc", StringComparison.Ordinal) && n != nameof(EventType.SpeechToTextDictation))
            .Select(Enum.Parse<EventType>).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());   // one name per value, apart from the old aliases

        // The name the WebSocket event map gives a Cuebot event is the enum value of the Streamer.bot trigger it imports
        // from. Exceptions: Cuebot triggers that stand for a neighbouring Streamer.bot trigger (custom code events also
        // fire the Custom trigger; webcam hot-plug imports as Activated/Deactivated; Wave Link 3 connection as the 1.x one;
        // remote signals as Remote Instance Trigger), whose WebSocket events keep their own names.
        int[] neighbours = [18001, 21351, 21352, 21017, 21018, 29003];
        var wrong = new List<string>();
        foreach (var rule in StreamerBotMappingTable.Default.Triggers)
        {
            foreach (var key in rule.TypeKeys.Where(k => int.TryParse(k, out var c) && StreamerBotTriggerCatalog.Paths.ContainsKey(c)))
            {
                var code = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                if (neighbours.Contains(code)) continue;
                if (StreamerBotTriggerCatalog.For(rule.TargetType, new Dictionary<string, object?>(), Settings(code))?.Code != code) continue;
                var name = StreamerBotEventMap.Map(rule.TargetType, Settings(code).ToDictionary(p => p.Key, p => (object?)p.Value));
                if (!Enum.TryParse<EventType>(name.Source + name.Type, out var type) && !Enum.TryParse(name.Type, out type)) continue;
                if ((int)type != code) wrong.Add($"{code} {rule.TargetType} -> {name} = {(int)type}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

        static Dictionary<string, string> Settings(int code) => code switch
        {
            120 or 124 => new() { ["platform"] = "Twitch" },
            4016 or 4017 => new() { ["platform"] = "YouTube" },
            35008 => new() { ["platform"] = "Kick" },
            180 or 181 => new() { ["platform"] = "Twitch", ["provider"] = "7TV" },
            182 or 183 => new() { ["platform"] = "Twitch", ["provider"] = "BTTV" },
            _ => [],
        };
    }

    [Fact]
    public void Every_documented_class_and_enum_exists()
    {
        var exported = typeof(CphProxy).Assembly.GetExportedTypes()
            .Where(t => t.Namespace is { } ns && (ns.StartsWith("Streamer.bot.", StringComparison.Ordinal) || ns.StartsWith("Twitch.", StringComparison.Ordinal)))
            .Select(t => t.Name.Split('`')[0])
            .ToHashSet(StringComparer.Ordinal);
        var documented = Names("types.txt").Select(n => n.Split('<')[0]).ToArray();

        Assert.Equal(35, documented.Length);
        Assert.Equal(Array.Empty<string>(), documented.Where(n => !exported.Contains(n)).ToArray());
    }
}
