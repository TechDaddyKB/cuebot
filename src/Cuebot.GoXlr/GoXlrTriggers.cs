using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.GoXlr.Common;
using Cuebot.GoXlr.Protocol;
using E = Cuebot.GoXlr.GoXlrEvents;

namespace Cuebot.GoXlr;

/// <summary>
/// GoXLR triggers (a Cuebot extra: Streamer.bot has no GoXLR integration). Each listens to one event type; the type id
/// is the event type. Type ids are stored in actions.json and must never change.
/// </summary>
internal sealed class GoXlrTriggers(GoXlrService goXlr) : ITriggerProvider
{
    public const string Category = "GoXLR";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor List(string key, string label, string help, Func<IEnumerable<string>> suggestions) =>
            new(key, label) { Help = help, Suggestions = suggestions };
        ParameterDescriptor Channels() => List("channel", "Channels", "Comma-separated channels (Mic, Line In, Console, System, Game, Chat, Sample, Music, Headphones, Mic Monitor, Line Out); leave empty for any",
            () => GoXlrNames.Channels.Select(GoXlrNames.Label));
        ParameterDescriptor Faders() => List("fader", "Faders", "Comma-separated faders (A, B, C, D); leave empty for any", () => GoXlrNames.Faders);
        ParameterDescriptor Device() => List("serial", "Devices", "Comma-separated GoXLR serial numbers; leave empty for any",
            () => goXlr.Status.Mixers.Select(m => m.Serial));
        ParameterDescriptor State(string on, string off) =>
            new("stateFilter", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", on, off] };

        static bool StateMatch(BotEvent e, ParameterValues s, string key, string on) => s["stateFilter"] switch
        {
            "Any" or "" => true,
            var v when v == on => e.Flag(key) == true,
            _ => e.Flag(key) == false,
        };
        static bool DeviceMatch(BotEvent e, ParameterValues s) => Steps.MatchesList(s["serial"], e.Text("goXlrSerial"));
        static bool ChannelMatch(BotEvent e, ParameterValues s) =>
            Steps.MatchesList(s["channel"], e.Text("goXlrChannel"), e.Text("goXlrChannelName"));
        static bool FaderMatch(BotEvent e, ParameterValues s) => Steps.MatchesList(s["fader"], e.Text("goXlrFader"));

        ITriggerType T(string type, string display, ParameterDescriptor[]? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
            new GoXlrTrigger(type, display, [Device(), .. parameters ?? []], (e, s) => DeviceMatch(e, s) && (filter?.Invoke(e, s) ?? true));

        return
        [
            new GoXlrTrigger(E.Connected, "Utility connected"),
            new GoXlrTrigger(E.Disconnected, "Utility disconnected"),
            T(E.DeviceConnected, "Device connected"),
            T(E.DeviceDisconnected, "Device disconnected"),
            T(E.VolumeChanged, "Volume changed", [Channels()], ChannelMatch),
            T(E.FaderAssignmentChanged, "Fader channel changed", [Faders(), Channels()], (e, s) => FaderMatch(e, s) && ChannelMatch(e, s)),
            T(E.FaderMuteChanged, "Fader mute changed", [Faders(), Channels(), State("Muted", "Unmuted")],
                (e, s) => FaderMatch(e, s) && ChannelMatch(e, s) && StateMatch(e, s, "goXlrMuted", "Muted")),
            T(E.CoughMuteChanged, "Cough / mic mute changed", [State("Muted", "Unmuted")], (e, s) => StateMatch(e, s, "goXlrMuted", "Muted")),
            T(E.ProfileChanged, "Profile changed",
                [List("profile", "Profiles", "Comma-separated profile names; leave empty for any", () => goXlr.Status.Profiles)],
                (e, s) => Steps.MatchesList(s["profile"], e.Text("goXlrProfile"))),
            T(E.MicProfileChanged, "Mic profile changed",
                [List("profile", "Mic profiles", "Comma-separated mic profile names; leave empty for any", () => goXlr.Status.MicProfiles)],
                (e, s) => Steps.MatchesList(s["profile"], e.Text("goXlrMicProfile"))),
            T(E.EffectStateChanged, "Effect turned on/off",
                [List("effect", "Effects", "Comma-separated effects (FX, Megaphone, Robot, Hard Tune); leave empty for any", () => GoXlrNames.Effects.Select(GoXlrNames.Label)),
                    State("On", "Off")],
                (e, s) => Steps.MatchesList(s["effect"], e.Text("goXlrEffect"), e.Text("goXlrEffectName")) && StateMatch(e, s, "goXlrEnabled", "On")),
            T(E.EffectPresetChanged, "Effect preset changed",
                [List("preset", "Presets", "Comma-separated preset numbers (1-6) or names; leave empty for any", () => GoXlrNames.Presets.Select(GoXlrNames.Label))],
                (e, s) => Steps.MatchesList(s["preset"], e.Text("goXlrPreset"), e.Text("goXlrPresetNumber"), e.Text("goXlrPresetName"))),
            T(E.SamplerBankChanged, "Sampler bank changed",
                [List("bank", "Banks", "Comma-separated banks (A, B, C); leave empty for any", () => GoXlrNames.SampleBanks)],
                (e, s) => Steps.MatchesList(s["bank"], e.Text("goXlrBank"))),
            T(E.SamplePlaybackChanged, "Sample started/stopped",
                [List("bank", "Banks", "Comma-separated banks (A, B, C); leave empty for any", () => GoXlrNames.SampleBanks),
                    List("button", "Sampler buttons", "Comma-separated buttons (Top Left, Top Right, Bottom Left, Bottom Right); leave empty for any",
                        () => GoXlrNames.SampleButtons.Select(GoXlrNames.Label)),
                    State("Playing", "Stopped")],
                (e, s) => Steps.MatchesList(s["bank"], e.Text("goXlrBank")) && Steps.MatchesList(s["button"], e.Text("goXlrSampleButton"))
                    && StateMatch(e, s, "goXlrPlaying", "Playing")),
            T(E.ButtonPressed, "Button pressed", [Buttons()], ButtonMatch),
            T(E.ButtonReleased, "Button released", [Buttons()], ButtonMatch),
            T(E.RoutingChanged, "Routing changed",
                [List("input", "Inputs", "Comma-separated inputs (Microphone, Chat, Music, Game, Console, Line In, System, Samples); leave empty for any",
                        () => GoXlrNames.Inputs.Select(GoXlrNames.Label)),
                    List("output", "Outputs", "Comma-separated outputs (Headphones, Broadcast Mix, Chat Mic, Sampler, Line Out, Stream Mix 2); leave empty for any",
                        () => GoXlrNames.Outputs.Select(GoXlrNames.Label)),
                    State("Routed", "Not routed")],
                (e, s) => Steps.MatchesList(s["input"], e.Text("goXlrInput")) && Steps.MatchesList(s["output"], e.Text("goXlrOutput"))
                    && StateMatch(e, s, "goXlrEnabled", "Routed")),
        ];

        ParameterDescriptor Buttons() => List("button", "Buttons",
            "Comma-separated buttons (Fader 1 Mute, Cough, Bleep, Effect Select 1, Effect Fx, Sampler Top Left, ...); leave empty for any",
            () => GoXlrNames.Buttons.Select(GoXlrNames.Label));
        static bool ButtonMatch(BotEvent e, ParameterValues s) => Steps.MatchesList(s["button"], e.Text("goXlrButton"), e.Text("goXlrButtonName"));
    }
}
