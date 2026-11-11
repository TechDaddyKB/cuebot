using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>Which ArtMeshes a color tint applies to (ColorTintRequest artMeshMatcher); empty everywhere means all of them.</summary>
public sealed record VtsTintMatcher
{
    public static VtsTintMatcher All { get; } = new();

    public IReadOnlyList<string> NameExact { get; init; } = [];
    public IReadOnlyList<string> NameContains { get; init; } = [];
    public IReadOnlyList<string> TagExact { get; init; } = [];
    public IReadOnlyList<string> TagContains { get; init; } = [];

    /// <summary>ArtMesh/layer group names or ids (names are looked up in the current model).</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>ArtMesh numbers (their order in the model).</summary>
    public IReadOnlyList<int> Numbers { get; init; } = [];

    public bool IsAll => NameExact.Count == 0 && NameContains.Count == 0 && TagExact.Count == 0 && TagContains.Count == 0
        && Groups.Count == 0 && Numbers.Count == 0;
}

/// <summary>Everything ItemLoadRequest can do, plus what Cuebot adds (pinning, timed unload, images from a URL or file).</summary>
public sealed record VtsItemLoad(string FileName)
{
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public double Size { get; init; } = 0.32;
    public double Rotation { get; init; }
    public double FadeTime { get; init; } = 0.5;
    public int? Order { get; init; }
    public bool UnloadWhenPluginDisconnects { get; init; } = true;
    public bool Flipped { get; init; }
    public bool Locked { get; init; }
    public bool Censored { get; init; }
    public double Smoothing { get; init; }

    /// <summary>An image to load instead of an item file: an http(s) URL, a file path, a data: URL or base64 PNG/JPG/GIF.</summary>
    public string Image { get; init; } = "";

    /// <summary>Show VTube Studio's "load this image?" popup first (only for <see cref="Image"/>).</summary>
    public bool AskFirst { get; init; }

    /// <summary>Pin the item to the model after loading.</summary>
    public VtsPinPlace Pin { get; init; } = VtsPinPlace.None;
    public string PinArtMesh { get; init; } = "";

    /// <summary>Unload the item again after this time.</summary>
    public TimeSpan? UnloadAfter { get; init; }
}

/// <summary>Where an item is pinned on the model.</summary>
public enum VtsPinPlace { None, Random, ArtMeshCenter, ArtMeshRandom, LastClick }

/// <summary>Model switching without naming a model.</summary>
public enum VtsModelChange { Next, Previous, Random, Unload }

/// <summary>Options of "Throw items at the model".</summary>
public sealed record VtsThrow
{
    /// <summary>Item files to throw (one is picked at random for each throw); ignored when <see cref="Images"/> is set.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Images to throw (URLs, file paths or data), for example a chatter's profile picture.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    public int Count { get; init; } = 1;
    public double Size { get; init; } = 0.15;

    /// <summary>Seconds between throws.</summary>
    public double Interval { get; init; } = 0.15;

    /// <summary>Seconds from the edge of the screen to the model.</summary>
    public double FlightTime { get; init; } = 0.35;

    /// <summary>0 (no reaction) to 1 (big flinch).</summary>
    public double Flinch { get; init; } = 0.5;

    /// <summary>"Random", "Left" or "Right": where the items come from.</summary>
    public string From { get; init; } = "Random";

    public bool AskFirst { get; init; }
}

public sealed partial class VTubeStudioService
{
    // Targets of timed changes ("for N seconds, then revert").
    private const string RevertModel = "model";
    private const string RevertMove = "move";
    private const string RevertExpression = "expression:";
    private const string RevertTint = "tint";
    private const string RevertVfx = "vfx";
    private const string RevertItem = "item:";

    private static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxImageBytes = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, bool> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly VtsParameterHolder _parameterHolder;
    private VtsArtMeshList _artMeshes = VtsArtMeshList.Empty;
    private VtsPhysics _physics = VtsPhysics.None;
    private string[] _vfxPresets = [];
    private string[] _vfxConfigIds = [];
    private TaskCompletionSource<JsonElement>? _outline;

    public VtsArtMeshList ArtMeshes => _artMeshes;
    public VtsPhysics Physics => _physics;
    public IReadOnlyList<string> VfxPresets => _vfxPresets;
    public IReadOnlyList<string> VfxConfigIds => _vfxConfigIds;

    /// <summary>The topmost ArtMesh at the last click on the model (for "pin where clicked").</summary>
    public VtsArtMeshHit? LastClick { get; private set; }

    // ---- ArtMeshes and color tints ----

    private async Task RefreshArtMeshesAsync(CancellationToken ct)
    {
        try
        {
            _artMeshes = VtsProtocol.ParseArtMeshes(await RequestAsync("ArtMeshListRequest", null, ct).ConfigureAwait(false));
        }
        catch (IntegrationRequestException)
        {
            _artMeshes = VtsArtMeshList.Empty;
        }
    }

    public async Task<VtsArtMeshList> GetArtMeshesAsync(CancellationToken ct = default)
    {
        await RefreshArtMeshesAsync(ct).ConfigureAwait(false);
        DataChanged?.Invoke();
        return _artMeshes;
    }

    /// <summary>
    /// Tints ArtMeshes; returns how many matched. Remembered (and re-applied after a reconnect) unless it is timed:
    /// with <paramref name="revertAfter"/> every tint goes back after that time to the remembered ones.
    /// </summary>
    public async Task<int> ColorTintAsync((int R, int G, int B, int A) color, VtsTintMatcher matcher, double mixWithSceneLighting,
        TimeSpan? revertAfter, CancellationToken ct = default, bool remember = true)
    {
        var request = new
        {
            colorTint = new
            {
                colorR = color.R, colorG = color.G, colorB = color.B, colorA = color.A,
                mixWithSceneLightingColor = Math.Clamp(mixWithSceneLighting, 0, 1),
            },
            artMeshMatcher = await MatcherAsync(matcher, ct).ConfigureAwait(false),
        };
        var data = await RequestAsync("ColorTintRequest", request, ct).ConfigureAwait(false);
        if (revertAfter is { } after)
        {
            _reverts.Schedule(RevertTint, after, RestoreTintsAsync);
        }
        else if (remember)
        {
            _reverts.Cancel(RevertTint);
            lock (_tints)
            {
                // A tint of everything replaces whatever was remembered before.
                if (matcher.IsAll) _tints.Clear();
                _tints.Add(request);
                if (_tints.Count > MaxRememberedTints) _tints.RemoveAt(0);
            }
        }
        return (int)(data.Num("matchedArtMeshes") ?? 0);
    }

    /// <summary>Resets every tint, then applies the remembered (untimed) ones again.</summary>
    private async Task RestoreTintsAsync(CancellationToken ct)
    {
        await ColorTintAsync((255, 255, 255, 255), VtsTintMatcher.All, 1, null, ct, remember: false).ConfigureAwait(false);
        object[] tints;
        lock (_tints) tints = [.. _tints];
        foreach (var tint in tints) await RequestAsync("ColorTintRequest", tint, ct).ConfigureAwait(false);
    }

    private async Task<object> MatcherAsync(VtsTintMatcher matcher, CancellationToken ct)
    {
        if (matcher.IsAll) return new { tintAll = true };
        string[]? groupIds = null;
        if (matcher.Groups.Count > 0)
        {
            if (_artMeshes.Groups.Count == 0) await RefreshArtMeshesAsync(ct).ConfigureAwait(false);
            groupIds = matcher.Groups.SelectMany(g =>
            {
                var byName = _artMeshes.Groups.Where(x => string.Equals(x.Name, g, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToArray();
                return byName.Length > 0 ? byName : [g];
            }).Distinct().ToArray();
        }
        return new
        {
            tintAll = false,
            artMeshNumber = matcher.Numbers.Count > 0 ? matcher.Numbers.ToArray() : null,
            nameExact = matcher.NameExact.Count > 0 ? matcher.NameExact.ToArray() : null,
            nameContains = matcher.NameContains.Count > 0 ? matcher.NameContains.ToArray() : null,
            tagExact = matcher.TagExact.Count > 0 ? matcher.TagExact.ToArray() : null,
            tagContains = matcher.TagContains.Count > 0 ? matcher.TagContains.ToArray() : null,
            artMeshGroupIDExact = groupIds,
        };
    }

    /// <summary>
    /// Cycles the tint through the rainbow for <paramref name="duration"/> (one full cycle every
    /// <paramref name="cycleSeconds"/>), then puts the remembered tints back.
    /// </summary>
    public async Task RainbowTintAsync(VtsTintMatcher matcher, TimeSpan duration, double cycleSeconds, double mixWithSceneLighting,
        CancellationToken ct = default)
    {
        var step = TimeSpan.FromMilliseconds(100);
        cycleSeconds = Math.Clamp(cycleSeconds, 0.2, 60);
        var started = _time.GetTimestamp();
        try
        {
            while (_time.GetElapsedTime(started) < duration)
            {
                var hue = _time.GetElapsedTime(started).TotalSeconds / cycleSeconds % 1.0;
                await ColorTintAsync(Hsv(hue), matcher, mixWithSceneLighting, null, ct, remember: false).ConfigureAwait(false);
                await Task.Delay(step, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (IsConnected) await RestoreTintsAsync(CancellationToken.None).ConfigureAwait(false);
        }

        static (int, int, int, int) Hsv(double h)
        {
            // Full saturation and value; VTube Studio can only darken, so pure hues look strongest.
            var i = (int)(h * 6) % 6;
            var f = h * 6 - Math.Floor(h * 6);
            int Up() => (int)Math.Round(255 * f);
            int Down() => (int)Math.Round(255 * (1 - f));
            return i switch
            {
                0 => (255, Up(), 0, 255),
                1 => (Down(), 255, 0, 255),
                2 => (0, 255, Up(), 255),
                3 => (0, Down(), 255, 255),
                4 => (Up(), 0, 255, 255),
                _ => (255, 0, Down(), 255),
            };
        }
    }

    /// <summary>
    /// Opens VTube Studio's ArtMesh picker and waits for the streamer to choose (OK) or cancel; returns the chosen ids
    /// (empty when cancelled).
    /// </summary>
    public async Task<IReadOnlyList<string>> PickArtMeshesAsync(string text, int count, IReadOnlyList<string> preselected, CancellationToken ct = default)
    {
        var data = await RequestWithPopupAsync("ArtMeshSelectionRequest", new
        {
            textOverride = text.Trim().Length >= 4 ? Truncate(text.Trim(), 1024) : null,
            helpOverride = (string?)null,
            requestedArtMeshCount = Math.Max(0, count),
            activeArtMeshes = preselected.ToArray(),
        }, ct).ConfigureAwait(false);
        return data.Bool("success") == true ? data.Array("activeArtMeshes").Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray() : [];
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    // ---- Physics ----

    private async Task RefreshPhysicsAsync(CancellationToken ct)
    {
        try
        {
            _physics = VtsProtocol.ParsePhysics(await RequestAsync("GetCurrentModelPhysicsRequest", null, ct).ConfigureAwait(false));
        }
        catch (IntegrationRequestException)
        {
            _physics = VtsPhysics.None;
        }
    }

    public async Task<VtsPhysics> GetPhysicsAsync(CancellationToken ct = default)
    {
        await RefreshPhysicsAsync(ct).ConfigureAwait(false);
        return _physics;
    }

    /// <summary>
    /// Overrides physics strength and/or wind for <paramref name="duration"/> (re-sent before VTube Studio's 5-second
    /// override timer runs out). Without <paramref name="groups"/> the values are the model's base values (0-100);
    /// with groups (names or ids) they are multipliers for those groups (0-2).
    /// </summary>
    public async Task PhysicsOverrideAsync(double? strength, double? wind, IReadOnlyList<string> groups, TimeSpan duration,
        CancellationToken ct = default)
    {
        if (strength is null && wind is null) throw new ArgumentException("Enter a strength or a wind value.");
        string[] ids = [];
        if (groups.Count > 0)
        {
            if (_physics.Groups.Count == 0) await RefreshPhysicsAsync(ct).ConfigureAwait(false);
            ids = groups.Select(g => _physics.Groups.FirstOrDefault(x => string.Equals(x.Name, g, StringComparison.OrdinalIgnoreCase))?.Id ?? g)
                .Distinct().ToArray();
        }
        var started = _time.GetTimestamp();
        while (true)
        {
            var left = duration - _time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return;
            var seconds = Math.Clamp(left.TotalSeconds, 0.5, 5);
            await RequestAsync("SetCurrentModelPhysicsRequest", new
            {
                strengthOverrides = Overrides(strength),
                windOverrides = Overrides(wind),
            }, ct).ConfigureAwait(false);
            if (left.TotalSeconds <= 5) return; // VTube Studio's own timer ends it
            await Task.Delay(TimeSpan.FromSeconds(4), _time, ct).ConfigureAwait(false);

            object[] Overrides(double? value) => value is not { } v ? []
                : ids.Length == 0
                    ? [new { id = "", value = Math.Clamp(v, 0, 100), setBaseValue = true, overrideSeconds = seconds }]
                    : ids.Select(id => (object)new { id, value = Math.Clamp(v, 0, 2), setBaseValue = false, overrideSeconds = seconds }).ToArray();
        }
    }

    // ---- Visual effects (post-processing) ----

    /// <summary>Reads the effect presets and effect setting names (both read from disk by VTube Studio, so only on connect and on request).</summary>
    private async Task RefreshEffectsAsync(CancellationToken ct)
    {
        try
        {
            var data = await RequestAsync("PostProcessingListRequest", new
            {
                fillPostProcessingPresetsArray = true,
                fillPostProcessingEffectsArray = true,
                effectIDFilter = Array.Empty<string>(),
            }, ct).ConfigureAwait(false);
            _vfxPresets = VtsProtocol.ParsePresets(data);
            _vfxConfigIds = data.Array("postProcessingEffects").SelectMany(e => e.Array("configEntries")).Select(c => c.Str("enumID"))
                .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IntegrationRequestException ex)
        {
            Log.Debug(Name, $"Visual effects unavailable: {ex.Message}");
        }
    }

    /// <summary>The visual effects state; <paramref name="refreshPresets"/> also re-reads the preset list.</summary>
    public async Task<VtsVfxState> GetVfxStateAsync(bool refreshPresets, CancellationToken ct = default)
    {
        var data = await RequestAsync("PostProcessingListRequest", new
        {
            fillPostProcessingPresetsArray = refreshPresets,
            fillPostProcessingEffectsArray = false,
            effectIDFilter = Array.Empty<string>(),
        }, ct).ConfigureAwait(false);
        if (refreshPresets)
        {
            _vfxPresets = VtsProtocol.ParsePresets(data);
            DataChanged?.Invoke();
        }
        return VtsProtocol.ParseVfx(data);
    }

    /// <summary>Loads a visual effects preset (turning effects on); with <paramref name="revertAfter"/> the previous effects come back.</summary>
    public async Task<VtsVfxState> LoadVfxPresetAsync(string preset, double fadeTime, TimeSpan? revertAfter, CancellationToken ct = default)
    {
        preset = Steps.Require(preset, "Preset");
        if (preset.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) preset = preset[..^5];
        await CaptureVfxForRevertAsync(revertAfter, fadeTime, ct).ConfigureAwait(false);
        var data = await RequestAsync("PostProcessingUpdateRequest", new
        {
            postProcessingOn = true,
            setPostProcessingPreset = true,
            setPostProcessingValues = false,
            presetToSet = preset,
            postProcessingFadeTime = Math.Clamp(fadeTime, 0, 2),
            setAllOtherValuesToDefault = false,
            usingRestrictedEffects = false,
            randomizeAll = false,
            randomizeAllChaosLevel = 0.0,
            postProcessingValues = Array.Empty<object>(),
        }, ct).ConfigureAwait(false);
        return VtsProtocol.ParseVfx(data);
    }

    /// <summary>Turns visual effects on or off (keeping their settings).</summary>
    public async Task<bool> SetVfxOnAsync(SwitchMode mode, CancellationToken ct = default, TimeSpan? revertAfter = null)
    {
        var current = await GetVfxStateAsync(refreshPresets: false, ct).ConfigureAwait(false);
        var on = Steps.Desired(mode, current.Active);
        await CaptureVfxForRevertAsync(revertAfter, 0.5, ct, current).ConfigureAwait(false);
        await RequestAsync("PostProcessingUpdateRequest", new
        {
            postProcessingOn = on,
            setPostProcessingPreset = false,
            setPostProcessingValues = false,
            presetToSet = "",
            postProcessingFadeTime = 0.0,
            setAllOtherValuesToDefault = false,
            usingRestrictedEffects = false,
            randomizeAll = false,
            randomizeAllChaosLevel = 0.0,
            postProcessingValues = Array.Empty<object>(),
        }, ct).ConfigureAwait(false);
        return on;
    }

    /// <summary>
    /// Sets individual effect settings ("Bloom_Strength=0.8, Bloom_StreakColorTint=220308FF"); with
    /// <paramref name="othersToDefault"/> every other effect fades back to its default (off).
    /// </summary>
    public async Task<VtsVfxState> SetVfxValuesAsync(string values, double fadeTime, bool othersToDefault, bool restricted, TimeSpan? revertAfter,
        CancellationToken ct = default)
    {
        var list = ParseVfxValues(values);
        await CaptureVfxForRevertAsync(revertAfter, fadeTime, ct).ConfigureAwait(false);
        var data = await RequestAsync("PostProcessingUpdateRequest", new
        {
            postProcessingOn = true,
            setPostProcessingPreset = false,
            setPostProcessingValues = true,
            presetToSet = "",
            postProcessingFadeTime = Math.Clamp(fadeTime, 0, 2),
            setAllOtherValuesToDefault = othersToDefault,
            usingRestrictedEffects = restricted,
            randomizeAll = false,
            randomizeAllChaosLevel = 0.0,
            postProcessingValues = list.Select(v => new { configID = v.Key, configValue = v.Value }).ToArray(),
        }, ct).ConfigureAwait(false);
        return VtsProtocol.ParseVfx(data);
    }

    /// <summary>Randomizes every effect (VTube Studio keeps it watchable); <paramref name="chaos"/> 0-1 is how wild.</summary>
    public async Task<VtsVfxState> RandomizeVfxAsync(double chaos, double fadeTime, TimeSpan? revertAfter, CancellationToken ct = default)
    {
        await CaptureVfxForRevertAsync(revertAfter, fadeTime, ct).ConfigureAwait(false);
        var data = await RequestAsync("PostProcessingUpdateRequest", new
        {
            postProcessingOn = true,
            setPostProcessingPreset = false,
            setPostProcessingValues = false,
            presetToSet = "",
            postProcessingFadeTime = Math.Clamp(fadeTime, 0, 2),
            setAllOtherValuesToDefault = false,
            usingRestrictedEffects = false,
            randomizeAll = true,
            randomizeAllChaosLevel = Math.Clamp(chaos, 0, 1),
            postProcessingValues = Array.Empty<object>(),
        }, ct).ConfigureAwait(false);
        return VtsProtocol.ParseVfx(data);
    }

    /// <summary>For a timed effect change: remembers the effects before the first change and schedules going back to them.</summary>
    private async Task CaptureVfxForRevertAsync(TimeSpan? revertAfter, double fadeTime, CancellationToken ct, VtsVfxState? current = null)
    {
        if (revertAfter is not { } after)
        {
            _reverts.Cancel(RevertVfx);
            return;
        }
        if (_reverts.IsPending(RevertVfx))
        {
            _reverts.Schedule(RevertVfx, after, _ => Task.CompletedTask); // keeps the first undo, restarts the timer
            return;
        }
        var before = current ?? await GetVfxStateAsync(refreshPresets: false, ct).ConfigureAwait(false);
        _reverts.Schedule(RevertVfx, after, token => RequestAsync("PostProcessingUpdateRequest", new
        {
            postProcessingOn = before.Active,
            setPostProcessingPreset = before.PresetActive,
            setPostProcessingValues = !before.PresetActive,
            presetToSet = before.PresetActive ? before.ActivePreset : "",
            postProcessingFadeTime = Math.Clamp(fadeTime, 0, 2),
            // Without a preset before, the change is undone by fading every effect back to its default.
            setAllOtherValuesToDefault = !before.PresetActive,
            usingRestrictedEffects = false,
            randomizeAll = false,
            randomizeAllChaosLevel = 0.0,
            postProcessingValues = Array.Empty<object>(),
        }, token));
    }

    /// <summary>"Bloom_Strength=0.8, Backlight_Strength=1" (also ";" or new lines) or a JSON object.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseVfxValues(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Enter at least one effect setting, e.g. Bloom_Strength=0.8.");
        if (text[0] == '{')
        {
            var json = JsonUtil.TryParse(text) is { ValueKind: JsonValueKind.Object } o ? o : throw new ArgumentException("Effect settings are not valid JSON.");
            return json.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.ValueKind == JsonValueKind.String
                ? p.Value.GetString() ?? "" : p.Value.GetRawText())).ToArray();
        }
        var list = new List<KeyValuePair<string, string>>();
        foreach (var entry in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"'{entry}' is not setting=value.");
            list.Add(KeyValuePair.Create(entry[..eq].Trim(), entry[(eq + 1)..].Trim()));
        }
        if (list.Select(v => v.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            throw new ArgumentException("Each effect setting can only appear once.");
        return list;
    }

    // ---- Permissions ----

    /// <summary>
    /// Asks VTube Studio for a permission (a popup the streamer answers; instant when already granted); returns
    /// whether Cuebot has it.
    /// </summary>
    public async Task<bool> RequestPermissionAsync(string permission, CancellationToken ct = default)
    {
        permission = Steps.Require(permission, "Permission");
        var data = await RequestWithPopupAsync("PermissionRequest", new { requestedPermission = permission }, ct).ConfigureAwait(false);
        foreach (var p in data.Array("permissions")) _permissions[p.Str("name")] = p.Bool("granted") ?? false;
        return _permissions.TryGetValue(permission, out var granted) && granted;
    }

    /// <summary>Asks for the permission once per session when Cuebot does not know it has it yet.</summary>
    private async Task EnsurePermissionAsync(string permission, CancellationToken ct)
    {
        if (_permissions.TryGetValue(permission, out var granted) && granted) return;
        if (!await RequestPermissionAsync(permission, ct).ConfigureAwait(false))
            throw new IntegrationRequestException($"VTube Studio did not grant Cuebot the {permission} permission.", VtsProtocol.ErrorRequiresPermission);
    }

    // ---- Custom parameters ----

    /// <summary>
    /// Creates (or updates) a custom tracking parameter that models can use as an input. Cuebot remembers it and
    /// creates it again on every connection.
    /// </summary>
    public async Task CreateCustomParameterAsync(VtsCustomParameter parameter, CancellationToken ct = default)
    {
        parameter.Name = VtsProtocol.CustomParameterName(parameter.Name);
        if (parameter.Explanation.Length > 255) parameter.Explanation = parameter.Explanation[..255];
        await CreateParameterCoreAsync(parameter, ct).ConfigureAwait(false);
        Settings.CustomParameters.RemoveAll(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
        Settings.CustomParameters.Add(parameter);
        SaveSettings();
        _parameters = [.. _parameters.Append(parameter.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
        DataChanged?.Invoke();
    }

    private Task CreateParameterCoreAsync(VtsCustomParameter p, CancellationToken ct) =>
        RequestAsync("ParameterCreationRequest", new
        {
            parameterName = p.Name,
            explanation = p.Explanation.Length > 0 ? p.Explanation : "Created by Cuebot",
            min = Math.Clamp(p.Min, -1_000_000, 1_000_000),
            max = Math.Clamp(p.Max, -1_000_000, 1_000_000),
            defaultValue = Math.Clamp(p.DefaultValue, -1_000_000, 1_000_000),
        }, ct);

    /// <summary>Deletes a custom parameter Cuebot created and forgets it.</summary>
    public async Task DeleteCustomParameterAsync(string name, CancellationToken ct = default)
    {
        name = Steps.Require(name, "Parameter name");
        _parameterHolder.Stop(name);
        await RequestAsync("ParameterDeletionRequest", new { parameterName = name }, ct).ConfigureAwait(false);
        Settings.CustomParameters.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        _parameters = [.. _parameters.Where(p => !string.Equals(p, name, StringComparison.OrdinalIgnoreCase))];
        DataChanged?.Invoke();
    }

    /// <summary>
    /// Keeps a parameter at a value (re-sent in the background twice a second) until it is set again, stopped, the
    /// <paramref name="duration"/> runs out or the connection ends. Good for values Cuebot gets now and then, like a
    /// heart rate or a goal's progress.
    /// </summary>
    public Task SetParameterAsync(string name, double value, double? weight, TimeSpan? duration, CancellationToken ct = default)
    {
        name = Steps.Require(name, "Parameter");
        VtsProtocol.ValidateInjection([new VtsParameterValue(name, value, weight)]);
        EnsureConnected();
        return _parameterHolder.SetAsync(name, value, weight, duration, ct);
    }

    /// <summary>Stops keeping a parameter (or every parameter, for an empty name); VTube Studio returns it to tracking within a second.</summary>
    public int StopParameter(string name) => name.Trim().Length == 0 ? _parameterHolder.Clear() : _parameterHolder.Stop(name.Trim());

    internal async Task SendHeldParametersAsync(IReadOnlyList<VtsParameterValue> values, CancellationToken ct)
    {
        var owner = _parameterHolder;
        foreach (var v in values) _parameterOwners.TryAdd(v.Id, owner);
        var mine = values.Where(v => _parameterOwners.TryGetValue(v.Id, out var o) && ReferenceEquals(o, owner)).ToArray();
        if (mine.Length == 0) return;
        await RequestAsync("InjectParameterDataRequest", new
        {
            faceFound = false,
            mode = "set",
            parameterValues = mine.Select(v => new { id = v.Id, value = v.Value, weight = v.Weight }).ToArray(),
        }, ct).ConfigureAwait(false);
    }

    internal void ReleaseHeldParameter(string name) =>
        _parameterOwners.TryRemove(new KeyValuePair<string, object>(name, _parameterHolder));

    // ---- Items ----

    /// <summary>
    /// Loads an item (an item file, or an image from a URL, file or data); returns its instance id. Optionally pins it
    /// to the model and unloads it again after <see cref="VtsItemLoad.UnloadAfter"/>.
    /// </summary>
    public async Task<string> LoadItemAsync(VtsItemLoad load, CancellationToken ct = default)
    {
        string fileName;
        string? custom = null;
        if (load.Image.Trim().Length > 0)
        {
            var (bytes, extension) = await ReadImageAsync(load.Image, ct).ConfigureAwait(false);
            custom = Convert.ToBase64String(bytes);
            fileName = "cuebot" + Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant() + extension;
            await EnsurePermissionAsync(VtsProtocol.PermissionCustomImages, ct).ConfigureAwait(false);
        }
        else
        {
            fileName = Steps.Require(load.FileName, "Item file");
        }
        object request = new
        {
            fileName,
            positionX = Math.Clamp(load.PositionX, -1000, 1000),
            positionY = Math.Clamp(load.PositionY, -1000, 1000),
            size = Math.Clamp(load.Size, 0, 1),
            rotation = load.Rotation,
            fadeTime = Math.Clamp(load.FadeTime, 0, 2),
            order = load.Order,
            failIfOrderTaken = false,
            smoothing = Math.Clamp(load.Smoothing, 0, 1),
            censored = load.Censored,
            flipped = load.Flipped,
            locked = load.Locked,
            unloadWhenPluginDisconnects = load.UnloadWhenPluginDisconnects,
            customDataBase64 = custom,
            customDataAskUserFirst = custom is not null && load.AskFirst,
            customDataSkipAskingUserIfWhitelisted = true,
            customDataAskTimer = -1,
        };
        var data = custom is not null && load.AskFirst
            ? await RequestWithPopupAsync("ItemLoadRequest", request, ct).ConfigureAwait(false)
            : await RequestAsync("ItemLoadRequest", request, ct).ConfigureAwait(false);
        var id = data.Str("instanceID");
        if (load.Pin != VtsPinPlace.None) await PinItemAsync(id, load.Pin, load.PinArtMesh, null, null, ct).ConfigureAwait(false);
        if (load.UnloadAfter is { } after)
            _reverts.Schedule(RevertItem + id, after, token => UnloadItemsAsync([id], [], false, token));
        return id;
    }

    /// <summary>Reads an image for a custom item: an http(s) URL, a data: URL, base64, or a file path. Returns its bytes and extension.</summary>
    internal static async Task<(byte[] Bytes, string Extension)> ReadImageAsync(string source, CancellationToken ct)
    {
        source = source.Trim();
        byte[] bytes;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await ImageClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new ArgumentException("The image is larger than 5 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes) throw new ArgumentException("The image is larger than 5 MB.");
            }
            bytes = buffer.ToArray();
        }
        else if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && source.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
        {
            bytes = Decode(source[(source.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) + 8)..]);
        }
        else if (File.Exists(source))
        {
            if (new FileInfo(source).Length > MaxImageBytes) throw new ArgumentException("The image is larger than 5 MB.");
            bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        }
        else
        {
            bytes = Decode(source);
        }
        if (bytes.Length > MaxImageBytes) throw new ArgumentException("The image is larger than 5 MB.");
        var extension = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0xFF, 0xD8, 0xFF, ..] => ".jpg",
            [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
            _ => throw new ArgumentException("The image must be a PNG, JPG or GIF."),
        };
        return (bytes, extension);

        static byte[] Decode(string base64)
        {
            try
            {
                return Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                throw new ArgumentException("The image is not a URL, an existing file or base64 data.");
            }
        }
    }

    /// <summary>The items in the scene (and, with <paramref name="includeFiles"/>, the item files: a disk read in VTube Studio).</summary>
    public async Task<IReadOnlyList<VtsItemInstance>> GetItemsAsync(bool includeFiles, CancellationToken ct = default)
    {
        await RefreshItemsAsync(includeFiles, ct).ConfigureAwait(false);
        return _items;
    }

    /// <summary>
    /// Pins an item (instance id or file name: every instance) to the model. <paramref name="size"/> is absolute (0-1),
    /// null keeps the size; <paramref name="angle"/> is relative to the model, null keeps the rotation.
    /// </summary>
    public async Task<int> PinItemAsync(string item, VtsPinPlace place, string artMesh, double? angle, double? size, CancellationToken ct = default)
    {
        var ids = await ResolveItemsAsync([Steps.Require(item, "Item")], ct).ConfigureAwait(false);
        var hit = LastClick;
        if (place == VtsPinPlace.LastClick && hit is null)
            throw new InvalidOperationException("Nobody clicked the model since Cuebot connected; click it in VTube Studio first.");
        if (place is VtsPinPlace.ArtMeshCenter or VtsPinPlace.ArtMeshRandom && artMesh.Trim().Length == 0)
            throw new ArgumentException("Enter the ArtMesh to pin to.");
        var count = 0;
        foreach (var id in ids)
        {
            await RequestAsync("ItemPinRequest", new
            {
                pin = true,
                itemInstanceID = id,
                angleRelativeTo = angle is null ? "RelativeToCurrentItemRotation" : "RelativeToModel",
                sizeRelativeTo = size is null ? "RelativeToCurrentItemSize" : "RelativeToWorld",
                vertexPinType = place switch
                {
                    VtsPinPlace.LastClick => "Provided",
                    VtsPinPlace.ArtMeshCenter => "Center",
                    _ => "Random",
                },
                pinInfo = new
                {
                    modelID = place == VtsPinPlace.LastClick ? hit!.ModelId : "",
                    artMeshID = place switch
                    {
                        VtsPinPlace.LastClick => hit!.ArtMeshId,
                        VtsPinPlace.ArtMeshCenter or VtsPinPlace.ArtMeshRandom => artMesh.Trim(),
                        _ => "",
                    },
                    angle = angle ?? 0,
                    size = size is { } s ? Math.Clamp(s, 0, 1) : 0,
                    vertexID1 = hit?.VertexId1 ?? 0,
                    vertexID2 = hit?.VertexId2 ?? 0,
                    vertexID3 = hit?.VertexId3 ?? 0,
                    vertexWeight1 = hit?.VertexWeight1 ?? 0,
                    vertexWeight2 = hit?.VertexWeight2 ?? 0,
                    vertexWeight3 = hit?.VertexWeight3 ?? 0,
                },
            }, ct).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    public async Task<int> UnpinItemAsync(string item, CancellationToken ct = default)
    {
        var ids = await ResolveItemsAsync([Steps.Require(item, "Item")], ct).ConfigureAwait(false);
        foreach (var id in ids) await RequestAsync("ItemPinRequest", new { pin = false, itemInstanceID = id }, ct).ConfigureAwait(false);
        return ids.Length;
    }

    /// <summary>Animation, brightness and opacity of an image or animated item (not Live2D items). Null values stay unchanged.</summary>
    public async Task<(int Frame, bool Playing)> ItemAnimationAsync(string item, double? framerate, int? frame, double? brightness, double? opacity,
        bool? play, IReadOnlyList<int>? autoStopFrames, CancellationToken ct = default)
    {
        var ids = await ResolveItemsAsync([Steps.Require(item, "Item")], ct).ConfigureAwait(false);
        (int, bool) last = default;
        foreach (var id in ids)
        {
            var data = await RequestAsync("ItemAnimationControlRequest", new
            {
                itemInstanceID = id,
                framerate = framerate ?? -1,
                frame = frame ?? -1,
                brightness = brightness is { } b ? Math.Clamp(b, 0, 1) : -1,
                opacity = opacity is { } o ? Math.Clamp(o, 0, 1) : -1,
                setAutoStopFrames = autoStopFrames is not null,
                autoStopFrames = autoStopFrames?.ToArray() ?? [],
                setAnimationPlayState = play is not null,
                animationPlayState = play ?? false,
            }, ct).ConfigureAwait(false);
            last = ((int)(data.Num("frame") ?? -1), data.Bool("animationPlaying") ?? false);
        }
        return last;
    }

    /// <summary>
    /// Puts an item between the model's layers (in front of an ArtMesh, or fully in front of/behind the model).
    /// <paramref name="front"/>: an ArtMesh id, "FullyInFront", "FullyInBack", or empty to take the item out of the model.
    /// </summary>
    public async Task SortItemAsync(string item, string front, string back, string splitAt, CancellationToken ct = default)
    {
        var ids = await ResolveItemsAsync([Steps.Require(item, "Item")], ct).ConfigureAwait(false);
        front = front.Trim();
        back = back.Trim();
        splitAt = splitAt.Trim();
        static string Kind(string value) => value.Length == 0 ? "Unchanged"
            : value is "FullyInFront" or "FullyInBack" ? "UseSpecialID" : "UseArtMeshID";
        foreach (var id in ids)
        {
            await RequestAsync("ItemSortRequest", new
            {
                itemInstanceID = id,
                frontOn = front.Length > 0,
                backOn = back.Length > 0,
                setSplitPoint = splitAt.Length > 0 ? "UseArtMeshID" : "Unchanged",
                setFrontOrder = Kind(front),
                setBackOrder = Kind(back),
                splitAt,
                withinModelOrderFront = front,
                withinModelOrderBack = back,
            }, ct).ConfigureAwait(false);
        }
    }

    // ---- Models, statistics, tracking, lighting, NDI ----

    /// <summary>Loads the next, previous or a random other model, or unloads the model; returns the new model's name ("" when unloaded).</summary>
    public async Task<string> ChangeModelAsync(VtsModelChange change, TimeSpan? revertAfter, CancellationToken ct = default)
    {
        if (change == VtsModelChange.Unload)
        {
            await LoadModelByIdAsync("", revertAfter, ct).ConfigureAwait(false);
            return "";
        }
        var models = (await GetModelsAsync(ct).ConfigureAwait(false)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (models.Length == 0) throw new InvalidOperationException("VTube Studio has no models.");
        var current = Array.FindIndex(models, m => m.Loaded);
        var others = models.Where((_, i) => i != current).ToArray();
        var next = change switch
        {
            VtsModelChange.Next => models[(current + 1 + models.Length) % models.Length],
            VtsModelChange.Previous => models[(current - 1 + models.Length * 2) % models.Length],
            _ => others.Length > 0 ? others[Random.Shared.Next(others.Length)] : models[0],
        };
        await LoadModelByIdAsync(next.Id, revertAfter, ct).ConfigureAwait(false);
        return next.Name;
    }

    public Task<JsonElement> GetStatisticsAsync(CancellationToken ct = default) => RequestAsync("StatisticsRequest", null, ct);

    public Task<JsonElement> GetSceneLightingAsync(CancellationToken ct = default) => RequestAsync("SceneColorOverlayInfoRequest", null, ct);

    /// <summary>Asks VTube Studio whether the face is found right now; the hands come from the last tracking event.</summary>
    public async Task<(bool Face, bool? LeftHand, bool? RightHand)> GetTrackingAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("FaceFoundRequest", null, ct).ConfigureAwait(false);
        return (data.Bool("found") ?? false, _tracking.LeftHandFound, _tracking.RightHandFound);
    }

    /// <summary>
    /// Turns NDI output on/off and optionally sets a fixed resolution (width a multiple of 16, height of 8, 256-8192).
    /// Needs the ControlNDISettings permission, which Cuebot asks for the first time.
    /// </summary>
    public async Task<bool> SetNdiAsync(SwitchMode mode, int? width, int? height, CancellationToken ct = default)
    {
        await EnsurePermissionAsync(VtsProtocol.PermissionNdi, ct).ConfigureAwait(false);
        var current = await RequestAsync("NDIConfigRequest", new { setNewConfig = false }, ct).ConfigureAwait(false);
        var on = Steps.Desired(mode, current.Bool("ndiActive") ?? false);
        var custom = width is not null && height is not null;
        if (custom && (width!.Value is < 256 or > 8192 || width.Value % 16 != 0 || height!.Value is < 256 or > 8192 || height.Value % 8 != 0))
            throw new ArgumentException("The NDI width must be a multiple of 16 and the height a multiple of 8, both 256-8192.");
        await RequestAsync("NDIConfigRequest", new
        {
            setNewConfig = true,
            ndiActive = on,
            useNDI5 = current.Bool("useNDI5") ?? true,
            useCustomResolution = custom || (current.Bool("useCustomResolution") ?? false),
            customWidthNDI = custom ? width!.Value : -1,
            customHeightNDI = custom ? height!.Value : -1,
        }, ct).ConfigureAwait(false);
        return on;
    }

    // ---- Throwing items at the model ----

    /// <summary>
    /// Throws items (item files or images) at the model's head: each flies in from the side, the model flinches, and
    /// the item falls away and is unloaded. Returns how many were thrown.
    /// </summary>
    public async Task<int> ThrowAsync(VtsThrow options, CancellationToken ct = default)
    {
        if (options.Files.Count == 0 && options.Images.Count == 0) throw new ArgumentException("Enter an item file or an image to throw.");
        var count = Math.Clamp(options.Count, 1, 50);
        if (options.Images.Count > 0)
        {
            // Read every image once for the whole volley (and ask for the permission once).
            var images = new List<string>();
            foreach (var image in options.Images.Where(i => i.Trim().Length > 0))
                images.Add(Convert.ToBase64String((await ReadImageAsync(image, ct).ConfigureAwait(false)).Bytes));
            options = options with { Images = images };
            await EnsurePermissionAsync(VtsProtocol.PermissionCustomImages, ct).ConfigureAwait(false);
        }
        var target = await HeadPositionAsync(ct).ConfigureAwait(false);
        var throws = new List<Task>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Interval, 0, 5)), _time, ct).ConfigureAwait(false);
            throws.Add(ThrowOneAsync(options, target, ct));
        }
        await Task.WhenAll(throws).ConfigureAwait(false);
        return count;
    }

    private async Task ThrowOneAsync(VtsThrow o, (double X, double Y) target, CancellationToken ct)
    {
        var side = o.From switch
        {
            "Left" => -1,
            "Right" => 1,
            _ => Random.Shared.Next(2) == 0 ? -1 : 1,
        };
        var useImage = o.Images.Count > 0;
        var source = useImage ? o.Images[Random.Shared.Next(o.Images.Count)] : o.Files[Random.Shared.Next(o.Files.Count)];
        var startY = target.Y + (Random.Shared.NextDouble() - 0.5) * 0.8;
        var id = await LoadItemAsync(new VtsItemLoad(useImage ? "" : source)
        {
            Image = useImage ? source : "",
            AskFirst = o.AskFirst,
            PositionX = side * 1.35,
            PositionY = startY,
            Size = Math.Clamp(o.Size, 0.01, 1),
            Rotation = Random.Shared.Next(360),
            FadeTime = 0,
            UnloadWhenPluginDisconnects = true,
        }, ct).ConfigureAwait(false);
        try
        {
            var flight = Math.Clamp(o.FlightTime, 0.05, 3);
            var hitX = target.X + (Random.Shared.NextDouble() - 0.5) * 0.12;
            var hitY = target.Y + (Random.Shared.NextDouble() - 0.5) * 0.12;
            await MoveItemsAsync([id], flight, "easeIn", hitX, hitY, null, Random.Shared.Next(360), null, null, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(flight), _time, ct).ConfigureAwait(false);
            var flinch = Math.Clamp(o.Flinch, 0, 1);
            var fall = MoveItemsAsync([id], 0.9, "easeIn", hitX + side * -0.35, -1.6, null, Random.Shared.Next(360), null, null, ct);
            if (flinch > 0)
            {
                // "add" mode: works alongside face tracking and other throwing plugins.
                await InjectParametersAsync(
                [
                    new VtsParameterValue("FaceAngleX", side * -25 * flinch, null),
                    new VtsParameterValue("FaceAngleZ", side * 15 * flinch, null),
                    new VtsParameterValue("FacePositionX", side * -8 * flinch, null),
                    new VtsParameterValue("EyeOpenLeft", -0.6 * flinch, null),
                    new VtsParameterValue("EyeOpenRight", -0.6 * flinch, null),
                ], add: true, faceFound: false, TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
            }
            await fall.ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(0.9), _time, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await UnloadItemsAsync([id], [], false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IntegrationRequestException or IntegrationNotConnectedException or TimeoutException)
            {
                // Loaded with unloadWhenPluginDisconnects, so VTube Studio cleans it up when Cuebot goes away.
            }
        }
    }

    /// <summary>Roughly where the model's head is, from one model outline (falls back to above the model's position).</summary>
    private async Task<(double X, double Y)> HeadPositionAsync(CancellationToken ct)
    {
        try
        {
            var outline = await GetOutlineAsync(ct).ConfigureAwait(false);
            var center = outline.Obj("convexHullCenter");
            var points = outline.Array("convexHull").Select(p => (X: p.Num("x") ?? 0, Y: p.Num("y") ?? 0)).ToArray();
            if (points.Length >= 3)
            {
                var cx = center.Num("x") ?? points.Average(p => p.X);
                var cy = center.Num("y") ?? points.Average(p => p.Y);
                var top = Math.Min(points.Max(p => p.Y), 1);
                return (Math.Clamp(cx, -1, 1), Math.Clamp(cy + (top - cy) * 0.6, -1, 1));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IntegrationRequestException)
        {
            Log.Debug(Name, $"No model outline: {ex.Message}");
        }
        var model = await GetCurrentModelAsync(ct).ConfigureAwait(false);
        return (Math.Clamp(model.Position.PositionX, -1, 1), Math.Clamp(model.Position.PositionY + 0.3, -1, 1));
    }

    /// <summary>One ModelOutlineEvent (subscribed just long enough to receive it; it is sent 15 times a second).</summary>
    private async Task<JsonElement> GetOutlineAsync(CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Interlocked.CompareExchange(ref _outline, waiter, null);
        if (current is not null) return await current.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        try
        {
            await RequestAsync("EventSubscriptionRequest", new { eventName = "ModelOutlineEvent", subscribe = true, config = new { draw = false } }, ct)
                .ConfigureAwait(false);
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _outline, null, waiter);
            try
            {
                await RequestAsync("EventSubscriptionRequest", new { eventName = "ModelOutlineEvent", subscribe = false, config = new { } }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IntegrationRequestException or IntegrationNotConnectedException or TimeoutException)
            {
                // Gone with the session.
            }
        }
    }

    private void OnOutline(JsonElement data) => _outline?.TrySetResult(data.Clone());

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
