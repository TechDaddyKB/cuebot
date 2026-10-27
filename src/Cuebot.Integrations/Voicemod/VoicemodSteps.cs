using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using static Cuebot.Integrations.Common.Steps;
using E = Cuebot.Integrations.Voicemod.VoicemodEvents;

namespace Cuebot.Integrations.Voicemod;

/// <summary>Voicemod triggers (category "Voicemod").</summary>
internal sealed class VoicemodTriggers(VoicemodService voicemod) : ITriggerProvider
{
    public const string Category = "Voicemod";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.VoiceChanged, "Voice changed",
            [new("voice", "Voices") { Help = "Names or ids, comma-separated; leave empty for any", Suggestions = () => voicemod.Voices.Select(v => v.DisplayName) }],
            (e, s) => MatchesList(s["voice"], e.Text("voiceName"), e.Text("voiceId"))),
        Trigger(Category, E.VoiceChangerToggled, "Voice changer toggled", [StateFilter()], (e, s) => MatchesState(e, s)),
        Trigger(Category, E.HearMyselfToggled, "Hear myself toggled", [StateFilter()], (e, s) => MatchesState(e, s)),
        Trigger(Category, E.BackgroundEffectsToggled, "Background effects toggled", [StateFilter()], (e, s) => MatchesState(e, s)),
        Trigger(Category, E.MuteToggled, "Microphone mute toggled", [StateFilter("Muted")], (e, s) => MatchesState(e, s, "muted")),
        Trigger(Category, E.SoundboardChanged, "Soundboard changed"),
    ];
}

/// <summary>Every Voicemod sub-action. Type ids are stored in actions.json and must never change.</summary>
internal static class VoicemodSubActions
{
    public const string Category = "Voicemod";

    public static IReadOnlyList<ISubActionHandler> Create(VoicemodService voicemod)
    {
        ParameterDescriptor Voice() => new("voice", "Voice (name or id)") { Suggestions = () => voicemod.Voices.Select(v => v.DisplayName) };
        ParameterDescriptor Sound() => new("sound", "Sound (name)") { Suggestions = () => voicemod.Sounds.Select(s => s.DisplayName) };

        return
        [
            Make("voicemod.selectVoice", "Select voice", [Voice()], async (ctx, v, ct) =>
            {
                var voice = await voicemod.SelectVoiceAsync(v["voice"], ct).ConfigureAwait(false);
                ctx.Args["voicemodVoiceId"] = voice.Id;
                ctx.Args["voicemodVoiceName"] = voice.DisplayName;
            }),
            Make("voicemod.selectRandomVoice", "Select random voice",
                [new("mode", "From", ParameterKind.Choice) { DefaultValue = "AllVoices", Choices = ["AllVoices", "FreeVoices", "FavoriteVoices", "CustomVoices"] }],
                (_, v, ct) => voicemod.SelectRandomVoiceAsync(v["mode"], ct)),
            Make("voicemod.getCurrentVoice", "Get current voice", [], async (ctx, _, ct) =>
            {
                var (id, name) = await voicemod.GetCurrentVoiceAsync(ct).ConfigureAwait(false);
                ctx.Args["voicemodVoiceId"] = id;
                ctx.Args["voicemodVoiceName"] = name;
            }),
            Make("voicemod.getVoices", "Get voices",
                [new("onlyFavorites", "Only favorites", ParameterKind.Boolean) { DefaultValue = "false" }],
                async (ctx, v, ct) =>
                {
                    var voices = (await voicemod.GetVoicesAsync(ct).ConfigureAwait(false))
                        .Where(x => !v.GetBool("onlyFavorites") || x.Favorited).ToArray();
                    ctx.Args["voicemodVoices"] = string.Join(", ", voices.Select(x => x.DisplayName));
                    ctx.Args["voicemodVoiceCount"] = voices.Length;
                    ctx.Args["voicemodVoicesJson"] = JsonUtil.Serialize(voices.Select(x => new { id = x.Id, name = x.DisplayName, favorite = x.Favorited, custom = x.IsCustom, enabled = x.Enabled }));
                }),
            Make("voicemod.setVoiceChanger", "Set voice changer state", [Switch()], async (ctx, v, ct) =>
                ctx.Args["voicemodVoiceChanger"] = await voicemod.SetSwitchAsync(VoicemodSwitch.VoiceChanger, ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("voicemod.setHearMyself", "Set hear my voice state", [Switch()], async (ctx, v, ct) =>
                ctx.Args["voicemodHearMyself"] = await voicemod.SetSwitchAsync(VoicemodSwitch.HearMyself, ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("voicemod.setBackgroundEffects", "Set background effect state", [Switch()], async (ctx, v, ct) =>
                ctx.Args["voicemodBackgroundEffects"] = await voicemod.SetSwitchAsync(VoicemodSwitch.BackgroundEffects, ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("voicemod.setMute", "Set microphone mute state", [Switch("state", "Muted")], async (ctx, v, ct) =>
                ctx.Args["voicemodMuted"] = await voicemod.SetSwitchAsync(VoicemodSwitch.MuteMic, ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("voicemod.playSound", "Play sound", [Sound()], async (ctx, v, ct) =>
            {
                var sound = await voicemod.PlaySoundAsync(v["sound"], ct).ConfigureAwait(false);
                ctx.Args["voicemodSoundName"] = sound.DisplayName;
            }),
            Make("voicemod.stopAllSounds", "Stop all sounds", [], (_, _, ct) => voicemod.StopAllSoundsAsync(ct)),
            Make("voicemod.getSounds", "Get sounds", [], async (ctx, _, ct) =>
            {
                var sounds = await voicemod.GetSoundsAsync(ct).ConfigureAwait(false);
                ctx.Args["voicemodSounds"] = string.Join(", ", sounds.Select(s => s.DisplayName));
                ctx.Args["voicemodSoundCount"] = sounds.Count;
                ctx.Args["voicemodSoundsJson"] = JsonUtil.Serialize(sounds.Select(s => new { name = s.DisplayName, fileName = s.FileName, type = s.Type }));
            }),
            Make("voicemod.setCensorBeep", "Set censor beep",
                [new("state", "Beep", ParameterKind.Choice) { DefaultValue = "On", Choices = ["On", "Off"] }],
                (_, v, ct) => voicemod.SetBeepAsync(v["state"] != "Off", ct)),
        ];
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new IntegrationSubAction(typeId, Category, name, parameters, run);
}
