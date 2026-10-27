using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

public class VoiceResolverTests
{
    private static readonly VoiceAlias First = new() { Name = "Narrator", EngineId = "a", VoiceId = "n", Rate = 2, Volume = 80 };
    private static readonly VoiceAlias Second = new() { Name = "Robot", EngineId = "A", VoiceId = "r" };

    private readonly FakeTtsEngine _engineA = new("a", available: true, "a-voice");

    [Fact]
    public async Task Finds_alias_by_name_case_insensitively()
    {
        var resolver = new VoiceResolver([_engineA]);

        var voice = await resolver.ResolveAsync(" robot ", [First, Second], CancellationToken.None);

        Assert.Same(_engineA, voice.Engine);
        Assert.Equal("r", voice.VoiceId);
        Assert.Equal("Robot", voice.AliasName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task Missing_alias_falls_back_to_first_alias(string? name)
    {
        var resolver = new VoiceResolver([_engineA]);

        var voice = await resolver.ResolveAsync(name, [First, Second], CancellationToken.None);

        Assert.Equal(new ResolvedVoice(_engineA, "n", 2, 80, "Narrator"), voice);
    }

    [Fact]
    public async Task Without_aliases_uses_first_available_engine_with_voices()
    {
        var unavailable = new FakeTtsEngine("off", available: false, "x");
        var silent = new FakeTtsEngine("silent", available: true);
        var resolver = new VoiceResolver([unavailable, silent, _engineA]);

        var voice = await resolver.ResolveAsync("anything", [], CancellationToken.None);

        Assert.Equal(new ResolvedVoice(_engineA, "a-voice", 0, 100, ""), voice);
    }

    [Fact]
    public async Task Throws_when_nothing_can_speak()
    {
        var resolver = new VoiceResolver([new FakeTtsEngine("off", available: false, "x")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(null, [], CancellationToken.None));
    }

    [Fact]
    public void Explicit_alias_with_unknown_or_unavailable_engine_throws()
    {
        var resolver = new VoiceResolver([new FakeTtsEngine("off", available: false)]);

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new VoiceAlias { Name = "x", EngineId = "missing" }));
        var error = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new VoiceAlias { Name = "x", EngineId = "off" }));
        Assert.Contains("fake engine disabled", error.Message);
    }
}
