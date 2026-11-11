using System.Text.Json;

namespace Cuebot.Donations.Streamlabs;

/// <summary>The REST features behind the Streamlabs steps and script functions (all need the connected app).</summary>
public sealed partial class StreamlabsService
{
    /// <summary>Who the connected app signed in as ("" when not connected).</summary>
    public string AccountName => Settings.AccountName;

    /// <summary>Refreshes the account name and linked platforms (GET /user).</summary>
    public async Task<StreamlabsUser> GetUserAsync(CancellationToken ct = default)
    {
        var user = await RestAsync((token, c) => _api.GetUserAsync(token, c), ct).ConfigureAwait(false);
        Settings.AccountName = user.DisplayName;
        Settings.LinkedAccounts = user.LinkedText;
        SaveSettings();
        return user;
    }

    public Task<IReadOnlyList<StreamlabsDonation>> GetDonationsAsync(int limit, string? currency = null, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetDonationsAsync(token, limit, null, null, currency, c), ct);

    /// <summary>Records a donation (POST /donations). At most 2 a minute, as Streamlabs asks.</summary>
    public Task<string> CreateDonationAsync(string name, string identifier, double amount, string currency, string message, bool skipAlert,
        CancellationToken ct = default) =>
        RestAsync((token, c) => _api.CreateDonationAsync(token, name, identifier, amount, currency, message, skipAlert, c), ct, alertLimit: true);

    /// <summary>Shows a custom alert in the Streamlabs alert box (POST /alerts). At most 2 a minute, as Streamlabs asks.</summary>
    public Task CreateAlertAsync(string type, string message, string userMessage, string imageHref, string soundHref, int durationMs,
        string specialTextColor, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.CreateAlertAsync(token, type, message, userMessage, imageHref, soundHref, durationMs, specialTextColor, c),
            ct, alertLimit: true);

    /// <summary>skip, mute_volume, unmute_volume, pause_queue, unpause_queue, show_video or hide_video.</summary>
    public Task AlertCommandAsync(string command, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.AlertCommandAsync(token, command, c), ct);

    public Task<bool> IsAlertQueuePausedAsync(CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetAlertQueuePausedAsync(token, c), ct);

    /// <summary>Shows Streamlabs' own test alert (follow, subscription, donation, host, bits, raid...) for a platform.</summary>
    public Task SendTestAlertAsync(string type, string platform, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.SendTestAlertAsync(token, type, platform, c), ct);

    public Task<IReadOnlyList<StreamlabsAlertProfile>> GetAlertProfilesAsync(CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetAlertProfilesAsync(token, c), ct);

    /// <summary>Activates an alert profile by name (looked up first) or id. Returns the profile activated.</summary>
    public async Task<StreamlabsAlertProfile> ActivateAlertProfileAsync(string profile, CancellationToken ct = default)
    {
        var wanted = profile.Trim();
        if (wanted.Length == 0) throw new StreamlabsApiException("Name the alert profile to switch to");
        var profiles = await GetAlertProfilesAsync(ct).ConfigureAwait(false);
        var match = profiles.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault(p => p.Id == wanted)
                    ?? throw new StreamlabsApiException($"No Streamlabs alert profile named '{wanted}'" +
                        (profiles.Count > 0 ? $" (there are: {string.Join(", ", profiles.Select(p => p.Name))})" : ""));
        await RestAsync((token, c) => _api.ActivateAlertProfileAsync(token, match.Id, c), ct).ConfigureAwait(false);
        return match;
    }

    /// <summary>Rolls the end credits. Streamlabs answers 422 when you are not live, reported as a readable error.</summary>
    public async Task RollCreditsAsync(CancellationToken ct = default)
    {
        try
        {
            await RestAsync((token, c) => _api.RollCreditsAsync(token, c), ct).ConfigureAwait(false);
        }
        catch (StreamlabsApiException ex) when (ex.Status == 422)
        {
            throw new StreamlabsApiException($"Streamlabs could not roll the credits; they only roll while you are live ({ex.Message})", 422);
        }
    }

    public Task EmptyJarAsync(CancellationToken ct = default) => RestAsync((token, c) => _api.EmptyJarAsync(token, c), ct);

    public Task SpinWheelAsync(CancellationToken ct = default) => RestAsync((token, c) => _api.SpinWheelAsync(token, c), ct);

    // ---- Cloudbot loyalty points ----

    /// <summary>The <c>channel</c> of the points calls: the setting, else the linked Twitch login.</summary>
    private string Channel(string? channel)
    {
        var value = channel?.Trim() is { Length: > 0 } given ? given : Settings.PointsChannel.Trim();
        return value.Length > 0 ? value : throw new StreamlabsApiException("Set the Streamlabs points channel first (Donations → Streamlabs)");
    }

    private static string RequireUser(string user) =>
        user.Trim().TrimStart('@') is { Length: > 0 } u ? u : throw new StreamlabsApiException("Name the viewer");

    public Task<long> GetPointsAsync(string user, string? channel = null, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetPointsAsync(token, RequireUser(user), Channel(channel), c), ct);

    /// <summary>Adds points (negative amounts subtract). Returns the new total.</summary>
    public async Task<long> AddPointsAsync(string user, long amount, string? channel = null, CancellationToken ct = default)
    {
        var name = RequireUser(user);
        var room = Channel(channel);
        if (amount < 0) return await RestAsync((token, c) => _api.SubtractPointsAsync(token, name, room, -amount, c), ct).ConfigureAwait(false);
        if (amount > 0)
            await RestAsync((token, c) => _api.ImportPointsAsync(token, room, new Dictionary<string, long> { [name] = amount }, c), ct).ConfigureAwait(false);
        return await GetPointsAsync(name, room, ct).ConfigureAwait(false);
    }

    public Task<long> SetPointsAsync(string user, long points, string? channel = null, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.SetPointsAsync(token, RequireUser(user), Channel(channel), points, c), ct);

    public Task AddPointsToAllAsync(long amount, string? channel = null, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.AddPointsToAllAsync(token, Channel(channel), amount, c), ct);

    public Task ResetPointsAsync(CancellationToken ct = default) => RestAsync((token, c) => _api.ResetPointsAsync(token, c), ct);

    public Task<IReadOnlyList<StreamlabsPointsRow>> GetPointsLeaderboardAsync(int limit, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetPointsLeaderboardAsync(token, limit, c), ct);

    // ---- Media share ----

    public Task MediaShareAsync(string command, int? volume = null, CancellationToken ct = default) =>
        RestAsync((token, c) => _api.MediaShareAsync(token, command, volume, c), ct);

    public Task<JsonElement> GetMediaShareSettingsAsync(CancellationToken ct = default) =>
        RestAsync((token, c) => _api.GetMediaShareSettingsAsync(token, c), ct);
}
