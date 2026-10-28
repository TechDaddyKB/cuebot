using System.Text.Json.Nodes;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Pronouns;

namespace Cuebot.Server.StreamerBot;

/// <summary>The optional Cuebot services the Streamer.bot payloads read. Building a payload never waits or goes online.</summary>
public sealed record StreamerBotPayloadServices(PronounService? Pronouns, EmoteService? Emotes);

/// <summary>
/// Pronouns in Streamer.bot's shape (GetUserPronouns <c>pronoun</c>, the official client's <c>StreamerbotUserPronouns</c>):
/// subject/object from alejo.io plus the possessive, reflexive and verb forms Streamer.bot derives from them.
/// </summary>
public static class StreamerBotPronouns
{
    /// <summary>Possessive, pronoun, reflexive, past/current tense and the verb's plural "s", by subject.</summary>
    private static readonly Dictionary<string, (string Possessive, string Pronoun, string Reflexive, bool Plural)> Forms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["he"] = ("His", "His", "Himself", false),
            ["she"] = ("Her", "Hers", "Herself", false),
            ["they"] = ("Their", "Theirs", "Themselves", true),
            ["it"] = ("Its", "Its", "Itself", false),
            ["xe"] = ("Xyr", "Xyrs", "Xemself", false),
            ["fae"] = ("Faer", "Faers", "Faerself", false),
            ["ae"] = ("Aer", "Aers", "Aerself", false),
            ["ve"] = ("Vis", "Vis", "Verself", false),
            ["zie"] = ("Hir", "Hirs", "Hirself", false),
            ["ze"] = ("Hir", "Hirs", "Hirself", false),
            ["per"] = ("Per", "Pers", "Perself", false),
            ["e"] = ("Eir", "Eirs", "Emself", false),
        };

    /// <summary>The documented object for <paramref name="found"/>; empty strings and <c>userFound: false</c> for null.</summary>
    public static JsonObject ToJson(UserPronouns? found)
    {
        var p = found?.Primary;
        var subject = p?.Subject ?? "";
        var obj = p?.Object ?? "";
        // Unknown sets ("Any", "Other") take the neutral "they" forms.
        var forms = Forms.TryGetValue(subject, out var f) ? f : Forms["they"];
        string Lower(string s) => s.ToLowerInvariant();
        var known = p is not null;
        var past = !known ? "" : forms.Plural ? "Were" : "Was";
        var current = !known ? "" : forms.Plural ? "Are" : "Is";
        var possessive = known ? forms.Possessive : "";
        var pronoun = known ? forms.Pronoun : "";
        var reflexive = known ? forms.Reflexive : "";
        return new JsonObject
        {
            ["pronounSubject"] = subject,
            ["pronounObject"] = obj,
            ["pronounPossessive"] = possessive,
            ["pronounPronoun"] = pronoun,
            ["pronounReflexive"] = reflexive,
            ["pronounPastTense"] = past,
            ["pronounSubjectLower"] = Lower(subject),
            ["pronounObjectLower"] = Lower(obj),
            ["pronounPossessiveLower"] = Lower(possessive),
            ["pronounPronounLower"] = Lower(pronoun),
            ["pronounReflexiveLower"] = Lower(reflexive),
            ["pronounPastTenseLower"] = Lower(past),
            ["pronounCurrentTense"] = current,
            ["pronounCurrentTenseLower"] = Lower(current),
            ["pronounPluralLower"] = known && !forms.Plural ? "s" : "",
            ["pronouns"] = found is null ? "" : $"({found.Display})",
            ["pronounsPlain"] = found?.Display ?? "",
            ["pronounLastCached"] = "",
            ["userFound"] = known,
        };
    }

    /// <summary>The cached pronouns of a Twitch login (no lookup is started); not found when unknown or not cached.</summary>
    public static JsonObject Cached(PronounService? service, string login)
    {
        if (service is null || login.Length == 0) return ToJson(null);
        var (known, found) = service.TryGetCached(login);
        return ToJson(known ? found : null);
    }
}
