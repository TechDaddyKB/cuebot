using Cuebot.GoXlr.Protocol;
using E = Cuebot.GoXlr.GoXlrEvents;

namespace Cuebot.GoXlr.Model;

/// <summary>
/// Turns two snapshots of one GoXLR into Cuebot events. Comparing snapshots (instead of reading patch paths) means a
/// patch that replaces a whole subtree, such as loading a profile, still fires exactly the events for what changed.
/// </summary>
internal static class GoXlrDiff
{
    public static List<(string Type, Dictionary<string, object?> Args)> Compare(GoXlrMixer? before, GoXlrMixer? after)
    {
        var events = new List<(string, Dictionary<string, object?>)>();
        if (before is null && after is null) return events;
        if (before is null)
        {
            events.Add((E.DeviceConnected, GoXlrArgs.Device(after!)));
            return events;
        }
        if (after is null)
        {
            events.Add((E.DeviceDisconnected, GoXlrArgs.Device(before)));
            return events;
        }

        void Add(string type, Action<Dictionary<string, object?>> fill)
        {
            var a = GoXlrArgs.Device(after);
            fill(a);
            events.Add((type, a));
        }

        if (before.ProfileName != after.ProfileName)
            Add(E.ProfileChanged, a => a["goXlrPreviousProfile"] = before.ProfileName);
        if (before.MicProfileName != after.MicProfileName)
            Add(E.MicProfileChanged, a => a["goXlrPreviousMicProfile"] = before.MicProfileName);

        foreach (var (name, now) in after.Faders)
        {
            if (!before.Faders.TryGetValue(name, out var was)) continue;
            if (was.Channel != now.Channel)
                Add(E.FaderAssignmentChanged, a =>
                {
                    Fader(a, now, after);
                    a["goXlrPreviousChannel"] = was.Channel;
                });
            if (was.MuteState != now.MuteState)
                Add(E.FaderMuteChanged, a =>
                {
                    Fader(a, now, after);
                    a["goXlrPreviousMuteState"] = was.MuteState;
                    a["goXlrWasMuted"] = was.Muted;
                });
        }

        foreach (var (channel, raw) in after.Volumes)
        {
            if (!before.Volumes.TryGetValue(channel, out var old) || old == raw) continue;
            Add(E.VolumeChanged, a =>
            {
                a["goXlrChannel"] = channel;
                a["goXlrChannelName"] = GoXlrNames.Label(channel);
                a["goXlrFader"] = after.FaderFor(channel)?.Name ?? "";
                a["goXlrVolume"] = GoXlrNames.Percent(raw);
                a["goXlrVolumeRaw"] = raw;
                a["goXlrPreviousVolume"] = GoXlrNames.Percent(old);
                a["goXlrPreviousVolumeRaw"] = old;
            });
        }

        if (before.Cough.MuteState != after.Cough.MuteState)
            Add(E.CoughMuteChanged, a =>
            {
                a["goXlrMuteState"] = after.Cough.MuteState;
                a["goXlrMuted"] = after.Cough.Muted;
                a["goXlrPreviousMuteState"] = before.Cough.MuteState;
                a["goXlrMuteFunction"] = after.Cough.MuteFunction;
            });

        if (before.Effects is { } be && after.Effects is { } ae)
        {
            foreach (var effect in GoXlrNames.Effects)
                if (be.IsEnabled(effect) != ae.IsEnabled(effect))
                    Add(E.EffectStateChanged, a =>
                    {
                        a["goXlrEffect"] = effect;
                        a["goXlrEffectName"] = GoXlrNames.Label(effect);
                        a["goXlrEnabled"] = ae.IsEnabled(effect);
                    });
            if (be.ActivePreset != ae.ActivePreset)
                Add(E.EffectPresetChanged, a =>
                {
                    GoXlrArgs.Preset(a, ae, ae.ActivePreset);
                    a["goXlrPreviousPreset"] = be.ActivePreset;
                    a["goXlrPreviousPresetName"] = be.PresetName(be.ActivePreset);
                });
        }

        if (before.Sampler is { } bs && after.Sampler is { } aS)
        {
            if (bs.ActiveBank != aS.ActiveBank)
                Add(E.SamplerBankChanged, a =>
                {
                    a["goXlrBank"] = aS.ActiveBank;
                    a["goXlrPreviousBank"] = bs.ActiveBank;
                });
            foreach (var (key, playing) in aS.Playing)
            {
                if (!bs.Playing.TryGetValue(key, out var was) || was == playing) continue;
                var parts = key.Split('/');
                Add(E.SamplePlaybackChanged, a =>
                {
                    a["goXlrBank"] = parts[0];
                    a["goXlrSampleButton"] = parts.Length > 1 ? parts[1] : "";
                    a["goXlrPlaying"] = playing;
                });
            }
        }

        foreach (var (button, down) in after.ButtonsDown)
        {
            if (!before.ButtonsDown.TryGetValue(button, out var was) || was == down) continue;
            Add(down ? E.ButtonPressed : E.ButtonReleased, a =>
            {
                a["goXlrButton"] = button;
                a["goXlrButtonName"] = GoXlrNames.Label(button);
                a["goXlrPressed"] = down;
            });
        }

        foreach (var (route, on) in after.Routing)
        {
            if (!before.Routing.TryGetValue(route, out var was) || was == on) continue;
            var parts = route.Split('/');
            Add(E.RoutingChanged, a =>
            {
                a["goXlrInput"] = parts[0];
                a["goXlrOutput"] = parts.Length > 1 ? parts[1] : "";
                a["goXlrEnabled"] = on;
            });
        }
        return events;
    }

    private static void Fader(Dictionary<string, object?> a, GoXlrFader f, GoXlrMixer m)
    {
        a["goXlrFader"] = f.Name;
        a["goXlrChannel"] = f.Channel;
        a["goXlrChannelName"] = GoXlrNames.Label(f.Channel);
        a["goXlrMuteState"] = f.MuteState;
        a["goXlrMuted"] = f.Muted;
        a["goXlrMuteFunction"] = f.MuteFunction;
        a["goXlrVolume"] = GoXlrNames.Percent(m.Volume(f.Channel));
    }
}
