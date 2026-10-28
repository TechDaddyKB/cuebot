using System.Reflection;
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
        Assert.Empty(documented.Where(n => !methods.Contains(n)));
    }

    [Fact]
    public void Every_documented_EventType_and_EventSource_value_exists()
    {
        var types = Names("event-types.txt");
        var sources = Names("event-sources.txt");

        Assert.Equal(508, types.Count);
        Assert.Equal(46, sources.Count);
        Assert.Empty(types.Where(n => !Enum.IsDefined(typeof(EventType), n)));
        Assert.Empty(sources.Where(n => !Enum.IsDefined(typeof(EventSource), n)));
        // Names earlier Cuebot versions used still parse, as aliases of the documented values.
        Assert.Equal(EventType.GlobalVariableCreated, Enum.Parse<EventType>("MiscGlobalVariableCreated"));
        Assert.Equal(EventType.SpeechToTextLog, Enum.Parse<EventType>("SpeechToTextDictation"));
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
        Assert.Empty(documented.Where(n => !exported.Contains(n)));
    }
}
