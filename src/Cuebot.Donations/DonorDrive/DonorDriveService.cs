using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Donations.DonorDrive;

/// <summary>
/// Polls a DonorDrive participant or team (Extra Life and other DonorDrive charity programs) through the public,
/// unauthenticated Public API: the profile with <c>If-None-Match</c> every <see cref="DonorDriveSettings.PollSeconds"/>
/// (at least 15 s, as DonorDrive asks), and the donations / incentives only when the profile changed. Donations that
/// existed before connecting are not replayed. 429 and errors back off; an unknown id stops with an error.
/// </summary>
public sealed class DonorDriveService : DonationServiceBase
{
    /// <summary>DonorDrive: "Please limit requests to one every 15 seconds."</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const int PageSize = 100;
    private const int DonationChecks = 4;

    private readonly DonorDriveApi _api;
    private readonly TimeSpan _minimumInterval;
    private readonly HashSet<string> _knownDonations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DonorDriveIncentive> _incentives = new(StringComparer.OrdinalIgnoreCase);
    private string _profileTag = "";
    private string _donationsTag = "";
    private int _donationChecksLeft;

    public DonorDriveService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, new DonorDriveApi(http), MinimumInterval)
    {
    }

    internal DonorDriveService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, DonorDriveApi api, TimeSpan minimumInterval)
        : base(settings, bus, log)
    {
        _api = api;
        _minimumInterval = minimumInterval;
    }

    public override string Name => DonorDriveEventMapper.Service;
    public DonorDriveSettings Settings => SettingsFile.Value.DonorDrive;

    /// <summary>The participant/team id plays the token's role (it is public, not a secret).</summary>
    protected override string Token => Settings.ProfileId;
    protected override string MissingTokenMessage => "Enter your DonorDrive participant or team id first.";
    private protected override string RedactSecrets(string? text) => text ?? "";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => false;
    protected override string ConnectedEvent => DonorDriveEventTypes.Connected;
    protected override string DisconnectedEvent => DonorDriveEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => DonorDriveEventMapper.TestKinds;

    /// <summary>The last profile read (null before the first successful poll).</summary>
    public DonorDriveProfile? Profile { get; private set; }

    /// <summary>Raised when <see cref="Profile"/> changes.</summary>
    public event Action? ProfileChanged;

    /// <summary>The API root that will be polled, e.g. https://dd.extra-life.org/api/ (throws for an invalid custom site).</summary>
    public Uri ApiBase => DonorDriveApi.BaseFor(Settings);

    /// <summary>Tests poll faster than DonorDrive allows real clients to.</summary>
    internal TimeSpan? IntervalOverride { get; set; }

    private TimeSpan Interval => IntervalOverride ?? (TimeSpan.FromSeconds(Math.Max(Settings.PollSeconds, 1)) < _minimumInterval
        ? _minimumInterval
        : TimeSpan.FromSeconds(Settings.PollSeconds));

    private string Currency => Settings.Currency.Trim() is { Length: > 0 } c ? c.ToUpperInvariant() : "USD";

    public override bool SendTestEvent(string kind)
    {
        using var participant = JsonDocument.Parse(DonorDriveEventMapper.SampleParticipant);
        using var sampleIncentive = JsonDocument.Parse(DonorDriveEventMapper.SampleIncentive);
        var profile = Profile ?? DonorDriveEventMapper.Profile(participant.RootElement, false)!;
        using var donation = JsonDocument.Parse(DonorDriveEventMapper.SampleDonation);
        var incentive = DonorDriveEventMapper.Incentive(sampleIncentive.RootElement);
        switch (kind.ToLowerInvariant())
        {
            case "donation":
                return Publish(DonorDriveEventMapper.Donation(donation.RootElement, profile, null, Currency, isTest: true));
            case "incentive":
                var e = DonorDriveEventMapper.Donation(donation.RootElement, profile, incentive, Currency, isTest: true);
                Publish(e);
                return Publish(DonorDriveEventMapper.IncentiveEvent(e, "TESTDONATION"));
            case "profile":
                return Publish(DonorDriveEventMapper.ProfileUpdated(profile with { Raised = profile.Raised + 25, NumDonations = profile.NumDonations + 1 },
                    profile, Currency, isTest: true));
            default:
                return false;
        }
    }

    private protected override Task PrepareAsync(CancellationToken ct)
    {
        _ = ApiBase;   // a bad custom site fails Connect right away
        _knownDonations.Clear();
        _incentives.Clear();
        _profileTag = "";
        _donationsTag = "";
        Profile = null;
        _donationChecksLeft = 0;
        return Task.CompletedTask;
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var backoff = Interval;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await PollAsync(first, ct).ConfigureAwait(false);
                first = false;
                backoff = Interval;
                wait = Interval;
                OnConnected($"Watching {(Profile?.Name is { Length: > 0 } n ? n : Settings.ProfileId)} on {ApiBase.Host}");
            }
            catch (DonorDriveApiException ex) when (ex.Status is 404 or 400)
            {
                throw new InvalidOperationException($"{ex.Message} ({ApiBase.Host}, {Settings.ProfileType.ToString().ToLowerInvariant()} {Settings.ProfileId}).");
            }
            catch (DonorDriveApiException ex) when (ex.Status is 429 or 503)
            {
                backoff = Min(TimeSpan.FromTicks(backoff.Ticks * 2), MaxBackoff);
                wait = ex.RetryAfter is { } retry && retry > backoff ? Min(retry, MaxBackoff) : backoff;
                Log.Warn(Name, $"{ex.Message}; waiting {wait.TotalSeconds:0}s");
                OnDisconnected("rate limited", fatal: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or DonorDriveApiException or JsonException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                backoff = Min(TimeSpan.FromTicks(backoff.Ticks * 2), MaxBackoff);
                wait = backoff;
                Log.Warn(Name, $"Poll failed ({ex.Message}); retrying in {wait.TotalSeconds:0}s");
                OnDisconnected(ex.Message, fatal: false);
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One poll (also used by tests). The first one only learns the current state.</summary>
    internal async Task PollAsync(bool first, CancellationToken ct)
    {
        var isTeam = Settings.ProfileType == DonorDriveProfileType.Team;
        var path = DonorDriveApi.ProfilePath(Settings.ProfileType, Settings.ProfileId);
        var response = await _api.GetAsync(ApiBase, path, _profileTag, ct).ConfigureAwait(false);
        _profileTag = response.ETag;
        var previous = Profile;
        if (!response.NotModified || first)
        {
            var profile = response.Body is { } body ? DonorDriveEventMapper.Profile(body, isTeam) : Profile;
            if (profile is null) throw new DonorDriveApiException("DonorDrive returned an unreadable profile");
            Profile = profile;
            if (previous != profile) ProfileChanged?.Invoke();
        }
        var current = Profile!;

        if (first)
        {
            await LoadIncentivesAsync(path, ct).ConfigureAwait(false);
            foreach (var d in await LoadDonationsAsync(path, ct).ConfigureAwait(false))
                _knownDonations.Add(DonorDriveEventMapper.DonationId(d));
            return;
        }
        if (previous is not null && previous != current)
        {
            Publish(DonorDriveEventMapper.ProfileUpdated(current, previous, Currency));
            // The donation list is cached separately (up to a minute), so keep looking for a few polls.
            if (previous.NumDonations != current.NumDonations || previous.Raised != current.Raised) _donationChecksLeft = DonationChecks;
        }
        if (_donationChecksLeft <= 0) return;

        var fresh = (await LoadDonationsAsync(path, ct).ConfigureAwait(false))
            .Where(d => DonorDriveEventMapper.DonationId(d) is { Length: > 0 } id && !_knownDonations.Contains(id))
            .Reverse()   // newest first from the API; publish oldest first
            .ToArray();
        _donationChecksLeft--;
        foreach (var d in fresh)
        {
            var id = DonorDriveEventMapper.DonationId(d);
            _knownDonations.Add(id);
            DonorDriveIncentive? incentive = null;
            if (d.Str("incentiveID") is { Length: > 0 } incentiveId)
            {
                if (!_incentives.ContainsKey(incentiveId)) await LoadIncentivesAsync(path, ct).ConfigureAwait(false);
                incentive = _incentives.GetValueOrDefault(incentiveId);
            }
            var donation = DonorDriveEventMapper.Donation(d, current, incentive, Currency);
            Publish(donation);
            if (incentive is not null) Publish(DonorDriveEventMapper.IncentiveEvent(donation, id));
        }
    }

    private async Task<JsonElement[]> LoadDonationsAsync(string profilePath, CancellationToken ct)
    {
        var response = await _api.GetAsync(ApiBase, $"{profilePath}/donations?limit={PageSize}", _donationsTag, ct).ConfigureAwait(false);
        _donationsTag = response.ETag;
        return response.Body is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object).ToArray()
            : [];
    }

    private async Task LoadIncentivesAsync(string profilePath, CancellationToken ct)
    {
        try
        {
            var response = await _api.GetAsync(ApiBase, $"{profilePath}/incentives?limit={PageSize}", "", ct).ConfigureAwait(false);
            if (response.Body is not { ValueKind: JsonValueKind.Array } list) return;
            foreach (var item in list.EnumerateArray())
                if (DonorDriveEventMapper.Incentive(item) is { } incentive) _incentives[incentive.Id] = incentive;
        }
        catch (DonorDriveApiException ex) when (ex.Status is 404 or 400)
        {
            // Some programs have no incentives; donations are still reported.
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
