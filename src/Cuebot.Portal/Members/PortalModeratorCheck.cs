using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Logging;

namespace Cuebot.Portal.Members;

/// <summary>
/// Enforces <see cref="PortalMember.RequireTwitchModerator"/>: a Twitch member with it set who no longer moderates the
/// streamer's Twitch channel is paused (<see cref="PortalMember.Suspended"/> with <see cref="Reason"/>), and resumed
/// when they moderate again. Checked on every portal request (the Twitch lookup is cached for a few minutes) and every
/// few minutes for everyone (so a paused member who is modded again can sign back in). When Cuebot cannot tell (signed
/// out of Twitch, the lookup failed) nothing changes: an error never removes access. A pause the streamer set by hand
/// is never lifted here.
/// </summary>
public sealed class PortalModeratorCheck(PortalDirectory directory, IEnumerable<IChannelRoles> roles, AppLog? log = null, TimeProvider? time = null)
    : IAsyncDisposable
{
    /// <summary>The pause reason this check sets, and the only one it lifts.</summary>
    public const string Reason = "No longer a moderator of your Twitch channel";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IChannelRoles? _twitch = roles.FirstOrDefault(r => string.Equals(r.Platform, "Twitch", StringComparison.OrdinalIgnoreCase));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    /// <summary>Re-checks one member; returns the member as it is now (null when removed).</summary>
    public async Task<PortalMember?> RefreshAsync(PortalMember member, CancellationToken ct = default)
    {
        var checkedByUs = member.Suspended && member.SuspendedReason == Reason;
        if (_twitch is null || member.Provider != IdentityProvider.Twitch || member.ProviderUserId.Length == 0) return member;
        if (!member.RequireTwitchModerator)
        {
            // The streamer switched the check off: lift only our own pause.
            if (checkedByUs) directory.SetSuspended(member.Id, false);
            return directory.Member(member.Id);
        }
        if (member.Suspended && !checkedByUs) return member;

        bool? isModerator;
        try
        {
            isModerator = await _twitch.CheckModeratorAsync(member.ProviderUserId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Warn("Portal", $"Could not check whether {member.Name} still moderates on Twitch: {ex.Message}");
            return member;
        }
        if (isModerator == false && !member.Suspended)
        {
            directory.SetSuspended(member.Id, true, Reason);
            log?.Info("Portal", $"Paused {member.Name}: no longer a moderator of the Twitch channel");
        }
        else if (isModerator == true && checkedByUs)
        {
            directory.SetSuspended(member.Id, false);
            log?.Info("Portal", $"Resumed {member.Name}: a moderator of the Twitch channel again");
        }
        return directory.Member(member.Id);
    }

    /// <summary>Checks every member once.</summary>
    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        foreach (var member in directory.Members) await RefreshAsync(member, ct).ConfigureAwait(false);
    }

    /// <summary>Starts checking everyone in the background (at start, then every <see cref="Interval"/>).</summary>
    public Task StartAsync()
    {
        if (_loop.IsCompleted) _loop = RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>Checks everyone every <see cref="Interval"/> until cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckAllAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Warn("Portal", $"Twitch moderator check failed: {ex.Message}");
                }
                await Task.Delay(Interval, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Portal members who signed in with Twitch: only the streamer may act against their Twitch account.</summary>
public sealed class PortalProtectedAccounts(PortalDirectory directory) : IProtectedAccounts
{
    public string? WhyProtected(string platform, string userId, string userName)
    {
        if (!string.Equals(platform, "Twitch", StringComparison.OrdinalIgnoreCase) || userId.Length == 0) return null;
        var member = directory.Members.FirstOrDefault(m => m.Provider == IdentityProvider.Twitch && m.ProviderUserId == userId);
        return member is null ? null : $"{member.Name} is a member of your mod portal. Only the streamer can do this.";
    }
}
