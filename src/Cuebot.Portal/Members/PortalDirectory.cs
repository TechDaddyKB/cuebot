using System.Security.Cryptography;
using Cuebot.Core.Actions;
using Cuebot.Portal.Crypto;

namespace Cuebot.Portal.Members;

/// <summary>Who signed in, as Cuebot read it from the provider after exchanging the sign-in code.</summary>
/// <param name="UserId">The provider's permanent user ID.</param>
/// <param name="Login">Twitch login, Google email or Discord user name: only for invite locks and the streamer's list.</param>
public sealed record ProviderIdentity(IdentityProvider Provider, string UserId, string Login, string DisplayName);

/// <summary>Why an invite or a sign-in was refused, in words for the mod.</summary>
public sealed class PortalAccessException(string message) : Exception(message);

/// <summary>A new invite: the secret appears only here, in the link the streamer hands out.</summary>
public sealed record CreatedInvite(PortalInvite Invite, string Secret, string Link);

/// <summary>
/// The mod portal's members, roles and invites (portal.json) and this Cuebot's identity key. Thread-safe; every
/// change is saved through the callback.
/// </summary>
public sealed class PortalDirectory
{
    public const string PortalOrigin = "https://portal.techdaddydigital.com";
    public static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxInviteLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan ProviderSignInLifetime = TimeSpan.FromDays(30);
    public const int MaxDevicesPerMember = 5;

    private readonly PortalSettings _settings;
    private readonly Action _save;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IdentityKey? _identity;

    public PortalDirectory(PortalSettings settings, Action save, TimeProvider? time = null)
    {
        _settings = settings;
        _save = save;
        _time = time ?? TimeProvider.System;
        lock (_gate)
        {
            if (_settings.Roles.Count == 0)
            {
                _settings.Roles.AddRange(DefaultRoles.Create());
                _save();
            }
        }
    }

    public event Action? Changed;

    /// <summary>This Cuebot's identity key, made (and saved) the first time it is needed.</summary>
    public IdentityKey Identity
    {
        get
        {
            lock (_gate)
            {
                if (_identity is not null) return _identity;
                if (_settings.IdentityKey.Length > 0)
                {
                    try { return _identity = IdentityKey.FromPkcs8(Convert.FromBase64String(_settings.IdentityKey)); }
                    catch (Exception e) when (e is FormatException or CryptographicException) { }
                }
                _identity = IdentityKey.Create();
                _settings.IdentityKey = Convert.ToBase64String(_identity.ExportPkcs8());
                _save();
                return _identity;
            }
        }
    }

    public string RelayId => Identity.RelayId;

    public IReadOnlyList<PortalRole> Roles { get { lock (_gate) return [.. _settings.Roles]; } }
    public IReadOnlyList<PortalMember> Members { get { lock (_gate) return [.. _settings.Members]; } }

    public IReadOnlyList<PortalInvite> Invites
    {
        get { lock (_gate) return [.. _settings.Invites.Where(i => i.ExpiresAt > _time.GetUtcNow())]; }
    }

    public PortalRole? Role(Guid id) { lock (_gate) return _settings.Roles.FirstOrDefault(r => r.Id == id); }
    public PortalMember? Member(Guid id) { lock (_gate) return _settings.Members.FirstOrDefault(m => m.Id == id); }

    /// <summary>The member's role, or null when the role was deleted (the member then can do nothing).</summary>
    public PortalRole? RoleOf(PortalMember member) => Role(member.RoleId);

    public ChangeMode ChangeModeOf(PortalMember member) => member.Changes ?? RoleOf(member)?.Changes ?? ChangeMode.NeedsApproval;

    public CreatedInvite CreateInvite(Guid roleId, string label = "", TimeSpan? lifetime = null, InviteLock? inviteLock = null)
    {
        var life = lifetime ?? DefaultInviteLifetime;
        if (life <= TimeSpan.Zero || life > MaxInviteLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime), "An invite lasts up to 30 days.");
        var secret = PortalKeys.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        PortalInvite invite;
        lock (_gate)
        {
            if (_settings.Roles.All(r => r.Id != roleId)) throw new ArgumentException("No such role.", nameof(roleId));
            invite = new PortalInvite
            {
                Label = label.Trim(),
                RoleId = roleId,
                SecretHash = Hash(secret),
                Lock = inviteLock is { Value.Length: > 0 } ? new InviteLock { Provider = inviteLock.Provider, Value = inviteLock.Value.Trim() } : null,
                CreatedAt = now,
                ExpiresAt = now + life,
            };
            _settings.Invites.RemoveAll(i => i.ExpiresAt <= now);
            _settings.Invites.Add(invite);
        }
        Saved();
        return new CreatedInvite(invite, secret, InviteLink(secret));
    }

    /// <summary>The link a mod opens. Everything after "#" stays in the browser: no server ever sees it.</summary>
    public string InviteLink(string secret) => $"{PortalOrigin}/#i={secret}&r={RelayId}&k={Identity.Fingerprint}";

    public bool WithdrawInvite(Guid inviteId)
    {
        bool removed;
        lock (_gate) removed = _settings.Invites.RemoveAll(i => i.Id == inviteId) > 0;
        if (removed) Saved();
        return removed;
    }

    /// <summary>
    /// Uses an invite: the signed-in account becomes a member with the invite's role, and the device is added.
    /// The invite is gone afterwards, whatever the outcome once its secret matched.
    /// </summary>
    public (PortalMember Member, PortalDevice Device) RedeemInvite(string secret, ProviderIdentity identity, byte[] devicePublicKey, string deviceName)
    {
        CheckDeviceKey(devicePublicKey);
        var now = _time.GetUtcNow();
        var hash = Hash(secret);
        PortalMember member;
        PortalDevice device;
        try
        {
            lock (_gate)
            {
                var invite = _settings.Invites.FirstOrDefault(i => CryptographicOperations.FixedTimeEquals(
                    PortalKeys.FromBase64Url(i.SecretHash), PortalKeys.FromBase64Url(hash)));
                if (invite is null) throw new PortalAccessException("This invite was already used, withdrawn or never existed. Ask the streamer for a new one.");
                _settings.Invites.Remove(invite);
                if (invite.ExpiresAt <= now) throw new PortalAccessException("This invite has expired. Ask the streamer for a new one.");
                if (invite.Lock is { } l && (l.Provider != identity.Provider || !string.Equals(l.Value, identity.Login, StringComparison.OrdinalIgnoreCase)))
                    throw new PortalAccessException($"This invite is for a different {l.Provider} account.");
                if (_settings.Roles.All(r => r.Id != invite.RoleId)) throw new PortalAccessException("The role this invite was for no longer exists.");
                if (FindMember(identity) is not null) throw new PortalAccessException("You are already a member. Sign in instead of using an invite.");

                member = new PortalMember
                {
                    Name = identity.DisplayName.Length > 0 ? identity.DisplayName : identity.Login,
                    Provider = identity.Provider,
                    ProviderUserId = identity.UserId,
                    RoleId = invite.RoleId,
                    JoinedAt = now,
                };
                device = NewDevice(devicePublicKey, deviceName, now);
                member.Devices.Add(device);
                _settings.Members.Add(member);
            }
        }
        finally
        {
            Saved();
        }
        return (member, device);
    }

    /// <summary>A member signs in again with their provider, on a known or a new device.</summary>
    public (PortalMember Member, PortalDevice Device) SignIn(ProviderIdentity identity, byte[] devicePublicKey, string deviceName)
    {
        CheckDeviceKey(devicePublicKey);
        var now = _time.GetUtcNow();
        var key = PortalKeys.Base64Url(devicePublicKey);
        PortalMember member;
        PortalDevice device;
        lock (_gate)
        {
            member = FindMember(identity) ?? throw new PortalAccessException("This account is not a member. Ask the streamer for an invite.");
            if (member.Suspended) throw new PortalAccessException("Your access is paused by the streamer.");
            var existing = member.Devices.FirstOrDefault(d => d.PublicKey == key);
            if (existing is null)
            {
                if (member.Devices.Count >= MaxDevicesPerMember)
                    member.Devices.Remove(member.Devices.MinBy(d => d.LastSeenAt)!);
                existing = NewDevice(devicePublicKey, deviceName, now);
                member.Devices.Add(existing);
            }
            existing.SignedInAt = now;
            existing.LastSeenAt = now;
            device = existing;
        }
        Saved();
        return (member, device);
    }

    /// <summary>
    /// A device reconnects with its key (proved by the caller). Null when the device is unknown, the member is
    /// suspended or removed, or the member must sign in with their provider again.
    /// </summary>
    public (PortalMember Member, PortalDevice Device)? Resume(byte[] devicePublicKey)
    {
        var key = PortalKeys.Base64Url(devicePublicKey);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var m in _settings.Members)
            {
                var d = m.Devices.FirstOrDefault(x => x.PublicKey == key);
                if (d is null) continue;
                if (m.Suspended || now - d.SignedInAt > ProviderSignInLifetime) return null;
                d.LastSeenAt = now;
                return (m, d);
            }
        }
        return null;
    }

    public PortalRole SaveRole(PortalRole role)
    {
        if (role.MaxRisk >= StepRisk.Never) role.MaxRisk = StepRisk.TrustedTech;
        role.Name = role.Name.Trim().Length > 0 ? role.Name.Trim() : "Role";
        role.Permissions = [.. role.Permissions.Distinct()];
        lock (_gate)
        {
            var i = _settings.Roles.FindIndex(r => r.Id == role.Id);
            if (i >= 0) _settings.Roles[i] = role;
            else _settings.Roles.Add(role);
        }
        Saved();
        return role;
    }

    /// <summary>Deletes a role nobody has and no open invite uses.</summary>
    public bool DeleteRole(Guid roleId)
    {
        lock (_gate)
        {
            if (_settings.Members.Any(m => m.RoleId == roleId) || _settings.Invites.Any(i => i.RoleId == roleId)) return false;
            if (_settings.Roles.RemoveAll(r => r.Id == roleId) == 0) return false;
        }
        Saved();
        return true;
    }

    /// <summary>Changes what a member may do. Null arguments keep the current value.</summary>
    public bool UpdateMember(Guid memberId, Guid? roleId = null, string? name = null, ChangeMode? changes = null, bool clearChanges = false,
        bool? requireTwitchModerator = null)
    {
        lock (_gate)
        {
            var m = _settings.Members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return false;
            if (roleId is { } r)
            {
                if (_settings.Roles.All(x => x.Id != r)) return false;
                m.RoleId = r;
            }
            if (name is not null && name.Trim().Length > 0) m.Name = name.Trim();
            if (clearChanges) m.Changes = null;
            else if (changes is not null) m.Changes = changes;
            if (requireTwitchModerator is { } t && m.Provider == IdentityProvider.Twitch) m.RequireTwitchModerator = t;
        }
        Saved();
        return true;
    }

    public bool SetSuspended(Guid memberId, bool suspended, string reason = "")
    {
        lock (_gate)
        {
            var m = _settings.Members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return false;
            m.Suspended = suspended;
            m.SuspendedReason = suspended ? reason : "";
        }
        Saved();
        return true;
    }

    public bool RemoveMember(Guid memberId)
    {
        bool removed;
        lock (_gate) removed = _settings.Members.RemoveAll(m => m.Id == memberId) > 0;
        if (removed) Saved();
        return removed;
    }

    public bool RemoveDevice(Guid memberId, Guid deviceId)
    {
        bool removed;
        lock (_gate) removed = _settings.Members.FirstOrDefault(m => m.Id == memberId)?.Devices.RemoveAll(d => d.Id == deviceId) > 0;
        if (removed) Saved();
        return removed;
    }

    /// <summary>Signs every member out on every device; they sign in with their provider again.</summary>
    public void SignOutEveryone()
    {
        lock (_gate) foreach (var m in _settings.Members) m.Devices.Clear();
        Saved();
    }

    private PortalMember? FindMember(ProviderIdentity identity) =>
        _settings.Members.FirstOrDefault(m => m.Provider == identity.Provider && m.ProviderUserId == identity.UserId);

    private static PortalDevice NewDevice(byte[] publicKey, string name, DateTimeOffset now) => new()
    {
        Name = name.Trim().Length > 0 ? name.Trim()[..Math.Min(name.Trim().Length, 60)] : "Device",
        PublicKey = PortalKeys.Base64Url(publicKey),
        AddedAt = now,
        LastSeenAt = now,
        SignedInAt = now,
    };

    private static void CheckDeviceKey(byte[] publicKey)
    {
        try { PortalKeys.FromRawPublicKey(publicKey); }
        catch (CryptographicException) { throw new PortalAccessException("The device key is not valid."); }
    }

    private static string Hash(string secret) => PortalKeys.Base64Url(SHA256.HashData(PortalKeys.Utf8(secret)));

    private void Saved()
    {
        _save();
        Changed?.Invoke();
    }
}
