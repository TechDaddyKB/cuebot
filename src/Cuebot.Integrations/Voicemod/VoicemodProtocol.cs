using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.Voicemod;

/// <summary>One inbound Voicemod Control API message, normalized (the API mixes actionType/type and actionObject/payload).</summary>
internal sealed record VoicemodMessage(string Kind, string? Id, JsonElement Body, JsonElement Root);

/// <summary>Envelopes and parsing for the Voicemod Control API (see PROTOCOL.md).</summary>
internal static class VoicemodProtocol
{
    public const int DefaultPort = 59129;

    /// <summary>Ports Voicemod may listen on; the docs ask clients to cycle through them.</summary>
    public static readonly int[] Ports = [59129, 20000, 39273, 42152, 43782, 46667, 35679, 37170, 38501, 33952, 30546];

    /// <summary>The response kind each status/list request is answered with.</summary>
    public static string? ResponseKind(string action) => action switch
    {
        "registerClient" => "registerClient",
        "getVoices" => "getVoices",
        "getCurrentVoice" => "getCurrentVoice",
        "getMemes" => "getMemes",
        "getAllSoundboard" => "getAllSoundboard",
        "getUser" => "getUser",
        "getUserLicense" => "getUserLicense",
        "getVoiceChangerStatus" => "toggleVoiceChanger",
        "getHearMyselfStatus" => "toggleHearMyVoice",
        "getBackgroundEffectStatus" => "toggleBackground",
        "getMuteMicStatus" => "toggleMuteMic",
        "getMuteMemeForMeStatus" => "toggleMuteMemeForMe",
        _ => null,
    };

    public static string Request(string action, string id, object? payload = null) =>
        JsonUtil.Serialize(new { action, id, payload = payload ?? new { } });

    public static VoicemodMessage? Parse(string text)
    {
        if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } root) return null;
        var kind = root.Str("actionType");
        if (kind.Length == 0) kind = root.Str("action");
        if (kind.Length == 0) kind = root.Str("type");
        if (kind.Length == 0) return null;
        if (kind == "toggleMute") kind = "toggleMuteMic"; // the schema and the examples disagree
        var id = root.Str("actionID", "actionId");
        if (id.Length == 0 && root.Prop("actionType") is null) id = root.Str("id");
        var body = root.Obj("actionObject");
        if (body.ValueKind != JsonValueKind.Object) body = root.Obj("payload");
        return new VoicemodMessage(kind, id.Length > 0 ? id : null, body, root);
    }

    /// <summary>registerClient: status 200 means authorized.</summary>
    public static (bool Ok, string Description) RegisterResult(VoicemodMessage message)
    {
        var status = message.Body.Obj("status");
        if (status.ValueKind != JsonValueKind.Object) status = message.Root.Obj("payload").Obj("status");
        var code = (int)(status.Num("code") ?? 0);
        var description = status.Str("description");
        return (code == 200, description.Length > 0 ? $"{code} {description}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static VoicemodVoice[] ParseVoices(VoicemodMessage message) =>
        message.Body.Array("voices")
            .Select(v => new VoicemodVoice(v.Str("id"), v.Str("friendlyName", "name"), v.Bool("enabled") ?? true,
                v.Bool("favorited") ?? false, v.Bool("isCustom") ?? false))
            .Where(v => v.Id.Length > 0)
            .ToArray();

    /// <summary>The current voice of getVoices (documented both inside and next to actionObject).</summary>
    public static string CurrentVoiceOf(VoicemodMessage message)
    {
        var current = message.Body.Str("currentVoice");
        return current.Length > 0 ? current : message.Root.Str("currentVoice");
    }

    public static VoicemodSound[] ParseMemes(VoicemodMessage message) =>
        message.Body.Array("listOfMemes")
            .Select(m => new VoicemodSound(m.Str("Name", "name"), m.Str("FileName", "fileName"), m.Str("Type", "type"), ""))
            .Where(m => m.FileName.Length > 0)
            .ToArray();

    public static VoicemodSound[] ParseSoundboards(VoicemodMessage message) =>
        message.Body.Array("soundboards")
            .SelectMany(b => b.Array("sounds").Select(s => new VoicemodSound(s.Str("name", "Name"), s.Str("id", "FileName"), s.Str("playbackMode"), b.Str("name"))))
            .Where(s => s.FileName.Length > 0)
            .ToArray();

    public static bool? Value(VoicemodMessage message) => message.Body.Bool("value");
}

public sealed record VoicemodVoice(string Id, string Name, bool Enabled, bool Favorited, bool IsCustom)
{
    public string DisplayName => Name.Length > 0 ? Name : Id;
}

/// <summary>A meme/soundboard sound. <see cref="FileName"/> is the id playMeme expects.</summary>
public sealed record VoicemodSound(string Name, string FileName, string Type, string Soundboard)
{
    public string DisplayName => Name.Length > 0 ? Name : FileName;
}
