using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Decks;
using Cuebot.Core.Points;
using Cuebot.Core.Quotes;
using Cuebot.Core.Variables;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's quote, points, credits, first-words, pronoun, toast, sound folder and Decks methods, on Cuebot's
/// core services and sub-actions. Methods returning data report a failure as the failure value with a warning; a
/// missing service throws.
/// </summary>
public sealed partial class CphProxy
{
    // ---- Quotes ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds a quote by a Twitch user (by id). The game is recorded only with <paramref name="captureGame"/>.
    /// Returns the new quote's id, or -1 for an empty quote.
    /// </summary>
    public int AddQuoteForTwitch(string userId, string quote, bool captureGame = false) => AddQuote("Twitch", userId, quote, captureGame);

    public int AddQuoteForYouTube(string userId, string quote) => AddQuote("YouTube", userId, quote, false);

    public int AddQuoteForKick(string userId, string quote, bool captureGame = false) => AddQuote("Kick", userId, quote, captureGame);

    /// <summary>The quote with that id, or null.</summary>
    public QuoteData? GetQuote(int quoteId) => Quotes().Get(quoteId) is { } q ? QuoteData(q) : null;

    public int GetQuoteCount() => Quotes().Count;

    /// <summary>Deletes a quote; false when there is none with that id.</summary>
    public bool DeleteQuote(int quoteId) => Quotes().Delete(quoteId) is not null;

    private int AddQuote(string platform, string userId, string text, bool captureGame, [CallerMemberName] string method = "")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            LogWarn($"{method}: the quote is empty");
            return -1;
        }
        var store = Quotes(method);
        var id = userId?.Trim() ?? "";
        var known = id.Length > 0 ? _services.Users?.Get(platform, id) : null;
        var added = store.Add(text, known?.DisplayName is { Length: > 0 } display ? display : known?.UserName ?? "", id, platform);
        if (!captureGame && added.Game.Length > 0) store.Update(added.Id, game: "");
        return added.Id;
    }

    private static QuoteData QuoteData(Quote quote) => new()
    {
        Id = quote.Id,
        Quote = quote.Text,
        User = quote.User,
        UserId = quote.UserId,
        Platform = quote.Platform,
        GameName = quote.Game,
        Timestamp = quote.CreatedAt.LocalDateTime,
    };

    private QuoteStore Quotes([CallerMemberName] string method = "") => Need(_services.Quotes, method);

    // ---- Points ---------------------------------------------------------------------------------------------------
    // Streamer.bot documents these only on its Points sub-action pages, without signatures. The shapes follow those
    // pages: a point definition (Cuebot's currency; null or "" is the default one), then the user by name and their
    // platform ("twitch", "youtube", ...; empty is the platform of the event that started the run).

    /// <summary>The user's balance; 0 when the user or definition is unknown.</summary>
    public long GetPoints(string pointDefinition, string user, string platform) =>
        Points(0L, (p, platformKey, id, _) => p.Get(platformKey, id, Currency(pointDefinition)), user, platform);

    /// <summary>Adds points (optionally multiplied by the user's/groups' multipliers); returns the new balance.</summary>
    public long AddPoints(string pointDefinition, string user, string platform, long amount, bool applyMultipliers = false) =>
        Points(0L, (p, platformKey, id, name) =>
        {
            var currency = Currency(pointDefinition);
            var add = applyMultipliers ? p.Multiplied(amount, platformKey, id, name, currency, null) : amount;
            return p.Add(platformKey, id, name, add, "cph", currency);
        }, user, platform);

    /// <summary>Adds <paramref name="amount"/> (negative removes) to the balance; returns the new balance.</summary>
    public long UpdatePoints(string pointDefinition, string user, string platform, long amount, bool applyMultipliers = false) =>
        AddPoints(pointDefinition, user, platform, amount, applyMultipliers);

    /// <summary>Adds <paramref name="amount"/> (negative removes) to every listed user's balance.</summary>
    public void UpdateBulkPoints(string pointDefinition, List<string> users, string platform, long amount, bool applyMultipliers = false)
    {
        foreach (var user in Ids(users)) AddPoints(pointDefinition, user, platform, amount, applyMultipliers);
    }

    /// <summary>Sets the balance; returns it.</summary>
    public long SetPoints(string pointDefinition, string user, string platform, long amount) =>
        Points(0L, (p, platformKey, id, name) => p.Set(platformKey, id, name, amount, Currency(pointDefinition)), user, platform);

    /// <summary>Takes <paramref name="amount"/> when the user has enough; false (nothing taken) otherwise.</summary>
    public bool SpendPoints(string pointDefinition, string user, string platform, long amount) =>
        Points(false, (p, platformKey, id, name) => amount >= 0 && p.TrySpend(platformKey, id, name, amount, out _, Currency(pointDefinition)),
            user, platform);

    /// <summary>Deletes the user's balance of the definition; false when they had none.</summary>
    public bool RemovePoints(string pointDefinition, string user, string platform) =>
        Points(false, (p, platformKey, id, _) => p.Delete(platformKey, id, Currency(pointDefinition)), user, platform);

    /// <summary>Sets the user's balance to 0.</summary>
    public void ResetUserPoints(string pointDefinition, string user, string platform) => SetPoints(pointDefinition, user, platform, 0);

    /// <summary>Moves points between users; false when the giver has too few or either user is unknown.</summary>
    public bool TransferPoints(string pointDefinition, string sourceUser, string sourcePlatform, string targetUser, string targetPlatform, long amount)
    {
        var points = Need(_services.Points);
        var from = PointsUser(points, sourceUser, sourcePlatform);
        var to = PointsUser(points, targetUser, string.IsNullOrWhiteSpace(targetPlatform) ? sourcePlatform : targetPlatform);
        if (from is null || to is null) return false;
        return Try(() => points.Transfer(from.Value.Platform, from.Value.Id, from.Value.Name, to.Value.Platform, to.Value.Id, to.Value.Name,
            amount, Currency(pointDefinition)));
    }

    /// <summary>Gives every known user (of <paramref name="platform"/>, or all) points; returns how many were paid.</summary>
    public int AwardPointsToAll(string pointDefinition, long amount, string? platform = null, bool onlyActive = false, bool applyMultipliers = false) =>
        PointsAdmin(0, p => p.AwardAll(string.IsNullOrWhiteSpace(platform) ? null : platform, amount, onlyActive, applyMultipliers, Currency(pointDefinition)));

    /// <summary>Gives every member of a user group points; returns how many were paid.</summary>
    public int AwardPointsToGroup(string pointDefinition, string groupName, long amount, bool applyMultipliers = false) =>
        PointsAdmin(0, p => p.AwardGroup(groupName ?? "", amount, applyMultipliers, Currency(pointDefinition)));

    /// <summary>The group's multiplier, or 1 when it has none.</summary>
    public double GetGroupMultiplier(string pointDefinition, string groupName) =>
        PointsAdmin(1d, p => (double)(p.GetGroupMultiplier(groupName ?? "", Currency(pointDefinition)) ?? 1m));

    public void SetGroupMultiplier(string pointDefinition, string groupName, double multiplier) =>
        PointsAdmin(true, p =>
        {
            p.SetGroupMultiplier(groupName ?? "", (decimal)multiplier, Currency(pointDefinition));
            return true;
        });

    public void RemoveGroupMultiplier(string pointDefinition, string groupName) =>
        PointsAdmin(true, p =>
        {
            p.SetGroupMultiplier(groupName ?? "", null, Currency(pointDefinition));
            return true;
        });

    /// <summary>The user's effective earning multiplier (their own, their groups' and their roles'), 1 when none applies.</summary>
    public double GetUserMultiplier(string pointDefinition, string user, string platform) =>
        Points(1d, (p, platformKey, id, name) => (double)p.Multiplier(platformKey, id, name, Currency(pointDefinition)), user, platform);

    /// <summary>The top balances, from place <paramref name="offset"/> + 1 (all when <paramref name="count"/> is 0 or less).</summary>
    public List<PointsLeaderboardEntry> GetLeaderboard(string pointDefinition, int count = 10, int offset = 0) =>
        PointsAdmin([], p =>
        {
            var skip = Math.Max(0, offset);
            var take = count <= 0 ? 0 : skip + count;
            return p.Leaderboard(null, take, Currency(pointDefinition)).Skip(skip).Select(e => new PointsLeaderboardEntry
            {
                Rank = e.Rank,
                Platform = e.Platform,
                UserId = e.UserId,
                UserName = e.UserName,
                Points = e.Points,
            }).ToList();
        });

    /// <summary>A point definition (Cuebot currency) by name; null when there is none.</summary>
    public PointDefinition? GetPointDefinition(string pointDefinition) =>
        Need(_services.Points).GetCurrency(Currency(pointDefinition)) is { } c ? Definition(c) : null;

    /// <summary>Every point definition, the default one first.</summary>
    public List<PointDefinition> GetPointDefinitions() => Need(_services.Points).Currencies.Select(Definition).ToList();

    /// <summary>Sets every balance of the definition back to 0.</summary>
    public void ResetPointDefinition(string pointDefinition) => PointsAdmin(0, p => p.ResetAll(null, Currency(pointDefinition)));

    private static PointDefinition Definition(PointsCurrency c) => new()
    {
        Name = c.Name,
        SingularName = c.SingularName,
        Symbol = c.Symbol,
        VariableName = c.VariableName,
        Enabled = c.Enabled,
        MaxBalance = c.MaxBalance,
        OnlyWhileLive = c.OnlyWhileLive,
    };

    private static string? Currency(string? pointDefinition) =>
        string.IsNullOrWhiteSpace(pointDefinition) ? null : pointDefinition.Trim();

    /// <summary>Runs a balance operation for a user given by name; <paramref name="failure"/> (with a warning) when it fails.</summary>
    private T Points<T>(T failure, Func<PointsService, string, string, string, T> operation, string user, string platform,
        [CallerMemberName] string method = "")
    {
        var points = Need(_services.Points, method);
        if (PointsUser(points, user, platform, method) is not { } who) return failure;
        try
        {
            return operation(points, who.Platform, who.Id, who.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            LogWarn($"{method}: {ex.Message}");
            return failure;
        }
    }

    private T PointsAdmin<T>(T failure, Func<PointsService, T> operation, [CallerMemberName] string method = "")
    {
        var points = Need(_services.Points, method);
        try
        {
            return operation(points);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            LogWarn($"{method}: {ex.Message}");
            return failure;
        }
    }

    /// <summary>Finds a user by name (or id) on a platform; null with a warning when Cuebot does not know them.</summary>
    private (string Platform, string Id, string Name)? PointsUser(PointsService points, string user, string platform,
        [CallerMemberName] string method = "")
    {
        var key = UserVariables.NormalizePlatform(string.IsNullOrWhiteSpace(platform) || platform.Contains('%')
            ? (_context is null ? null : UserVariables.PlatformFromArgs(_context.Args)) ?? "twitch"
            : platform.Trim());
        var name = Login(user);
        if (name.Length == 0)
        {
            LogWarn($"{method}: no user given");
            return null;
        }
        var id = points.FindUserId(key, name)
                 ?? _services.Users?.FindByName(key, name)?.UserId
                 ?? (_services.Users?.Get(key, name) is { } byId ? byId.UserId : null);
        if (id is not { Length: > 0 })
        {
            LogWarn($"{method}: unknown {key} user '{name}' (they need to chat or have points first)");
            return null;
        }
        return (key, id, name);
    }

    // ---- Credits, first words, pronouns ---------------------------------------------------------------------------

    /// <summary>Clears the credits collected so far.</summary>
    public void ResetCredits() => Call("credits.reset", null);

    /// <summary>Forgets who has chatted, on every platform, so everyone's next message fires First Words again.</summary>
    public void ResetFirstWords() => Need(_services.Users).ResetFirstWords();

    /// <summary>
    /// A Twitch user's pronouns from alejo.io, with the keys of Cuebot's "Get pronouns" sub-action (hasPronouns,
    /// pronoun, pronounSubject, pronounObject, pronounSingular, pronounAltSubject, pronounAltObject); null on failure.
    /// </summary>
    public Dictionary<string, object?>? PronounLookup(string userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin)) return null;
        var found = TryRunSubActionForArgs("pronouns.addForUser", new Dictionary<string, string> { ["user"] = Login(userLogin), ["prefix"] = "" });
        if (found is null) return null;
        string[] keys = ["hasPronouns", "pronoun", "pronounSubject", "pronounObject", "pronounSingular", "pronounAltSubject", "pronounAltObject"];
        return keys.ToDictionary(k => k, k => found.GetValueOrDefault(k));
    }

    /// <summary>Forgets cached pronouns so the next lookup asks alejo.io again (Cuebot clears every cached user).</summary>
    public void PronounClearForUserlogin(string userLogin) => Need(_services.Pronouns).ClearCache();

    // ---- Toasts and sounds ----------------------------------------------------------------------------------------

    /// <summary>
    /// Shows a desktop notification. Cuebot's notifications have no attribution line, so <paramref name="attribution"/>
    /// is added under the message.
    /// </summary>
    public void ShowToastNotification(string title, string message, string? attribution = null, string? iconPath = null) =>
        Toast("", title, message, attribution, iconPath);

    /// <summary>Shows a desktop notification with an id, reported as <c>toastId</c> when it is clicked.</summary>
    public void ShowToastNotification(string id, string title, string message, string? attribution, string? iconPath = null) =>
        Toast(id, title, message, attribution, iconPath);

    private void Toast(string id, string title, string message, string? attribution, string? iconPath, [CallerMemberName] string method = "")
    {
        var text = string.IsNullOrWhiteSpace(attribution) ? message ?? "" : $"{message}\n{attribution}";
        Call("desktop.notify", new { title = title ?? "", message = text, icon = iconPath ?? "", id = id ?? "" }, method);
    }

    /// <summary>
    /// Plays a random sound file (wav, mp3, ogg, flac) from <paramref name="path"/>, with <paramref name="recursive"/>
    /// its subfolders too. Returns the sound's length in seconds (0 when the folder has none).
    /// </summary>
    public double PlaySoundFromFolder(string path, float volume = 1.0f, bool recursive = false, bool finishBeforeContinuing = false,
        string? name = null, bool useFileName = true)
    {
        var played = RunSubActionForArgs("audio.playSoundFromFolder", new Dictionary<string, string>
        {
            ["folder"] = path ?? "",
            ["extensions"] = "",
            ["subfolders"] = recursive ? "true" : "false",
            ["device"] = "",
            ["volume"] = Math.Clamp((int)Math.Round(volume * 100), 0, 100).ToString(CultureInfo.InvariantCulture),
            ["wait"] = finishBeforeContinuing ? "true" : "false",
            ["tag"] = name ?? "",
            ["useFileName"] = string.IsNullOrWhiteSpace(name) && useFileName ? "true" : "false",
        });
        return CphConvert.TryTo(played.GetValueOrDefault("soundDuration"), out double seconds) ? seconds : 0;
    }

    // ---- Decks ----------------------------------------------------------------------------------------------------
    // Decks and items are found by id or name. States count from 0; a null state changes the default (0) state.
    // Cuebot's Decks have no uploaded files, so fileId (Streamer.bot's file store) is used as an image URL or path.

    public void DeckItemSetBackground(string deckId, string itemId, string? color = null, string? url = null, string? fileId = null, int? state = null) =>
        Decks().SetBackground(deckId, itemId, Blank(color), Blank(url) ?? Blank(fileId), Math.Max(0, state ?? 0));

    public void DeckItemSetIcon(string deckId, string itemId, string? color = null, string? name = null, string? url = null, string? fileId = null,
        int? state = null) =>
        Decks().SetIcon(deckId, itemId, Blank(color), Blank(name), Blank(url) ?? Blank(fileId), Math.Max(0, state ?? 0));

    public void DeckItemSetState(string deckId, string itemId, int state) => Decks().SetState(deckId, itemId, Math.Max(0, state));

    public void DeckItemSetTitle(string deckId, string itemId, string title, string? color = null, int? state = null) =>
        Decks().SetTitle(deckId, itemId, title ?? "", Blank(color), Math.Max(0, state ?? 0));

    public void DeckItemSetValue(string deckId, string itemId, int value, int? state = null) =>
        Decks().SetValue(deckId, itemId, Num(value), Math.Max(0, state ?? 0));

    /// <summary>Switches the item between state 0 and 1.</summary>
    public void DeckItemToggleState(string deckId, string itemId) => Decks().ToggleState(deckId, itemId);

    /// <summary>Shows a notification on the deck. Cuebot's deck notifications have no colour or sound, so those are ignored.</summary>
    public void DeckNotify(string deckId, string title, string description, string? color = null, string? soundId = null) =>
        Decks().Notify(deckId, title ?? "", description ?? "");

    public void DeckPageNext(string deckId) => Decks().ChangePage(deckId, 1);
    public void DeckPagePrev(string deckId) => Decks().ChangePage(deckId, -1);

    /// <summary>Shows a page, counting from 1 like the Decks editor.</summary>
    public void DeckPageSet(string deckId, int page) => Decks().ChangePage(deckId, null, Math.Max(1, page));

    private DeckService Decks([CallerMemberName] string method = "") => Need(_services.Decks, method);

    private static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a registered sub-action inside this run and returns the arguments right after it; the run's arguments
    /// are then put back (like <see cref="CallForArgs"/>).
    /// </summary>
    private Dictionary<string, object?> RunSubActionForArgs(string typeId, Dictionary<string, string> parameters, [CallerMemberName] string method = "")
    {
        var context = RequireContext(method);
        var before = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        try
        {
            RunSubAction(typeId, parameters, method);
            return new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var key in context.Args.Keys.Where(k => !before.ContainsKey(k)).ToList()) context.Args.Remove(key);
            foreach (var (key, value) in before) context.Args[key] = value;
        }
    }

    /// <summary><see cref="RunSubActionForArgs"/>, but null (with a warning) when the sub-action failed.</summary>
    private Dictionary<string, object?>? TryRunSubActionForArgs(string typeId, Dictionary<string, string> parameters,
        [CallerMemberName] string method = "")
    {
        try
        {
            return RunSubActionForArgs(typeId, parameters, method);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            LogWarn($"{method}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs a sub-action; false (with a warning) when it failed.</summary>
    private bool TrySubAction(string typeId, Dictionary<string, string> parameters, [CallerMemberName] string method = "") =>
        TryRunSubActionForArgs(typeId, parameters, method) is not null;

    private static string Json(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? "" : element.GetRawText();
}
