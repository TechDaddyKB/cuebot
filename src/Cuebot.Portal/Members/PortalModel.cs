using Cuebot.Core.Actions;
using Cuebot.Core.Storage;

namespace Cuebot.Portal.Members;

/// <summary>What a role lets its members do in the portal (docs/MOD_PORTAL_PLAN.md, section 5).</summary>
public enum PortalPermission
{
    /// <summary>Run the actions the role's scope allows (running is not a change and never waits for approval).</summary>
    RunActions,
    CreateCommands,
    EditCommands,
    DeleteCommands,
    CreateActions,
    EditActions,
    DeleteActions,
    /// <summary>Volume and mute controls.</summary>
    AudioLevels,
    /// <summary>Scenes, raids, queues and TTS settings from the portal's own controls.</summary>
    StreamControl,
    /// <summary>End the stream, ban, change title and category (with a second confirm).</summary>
    ShowCritical,
    UploadSounds,
    /// <summary>Images, GIFs and video.</summary>
    UploadMedia,
    EditOverlays,
    CreateOverlays,
    ViewActivity,
    /// <summary>Scrubbed logs and connection status.</summary>
    ViewLogs,
    /// <summary>Undo changes other members made (everyone may undo their own).</summary>
    UndoOthers,
}

/// <summary>Whether a member's changes go live at once.</summary>
public enum ChangeMode
{
    Live,
    /// <summary>Live, and the streamer gets a notification.</summary>
    LiveAndNotify,
    NeedsApproval,
}

/// <summary>A sign-in provider. Build order: Google, Twitch, Discord.</summary>
public enum IdentityProvider
{
    Google,
    Twitch,
    Discord,
}

public sealed class PortalRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New role";

    /// <summary>The riskiest step and trigger types members may add, edit or run. Never above Trusted tech.</summary>
    public StepRisk MaxRisk { get; set; } = StepRisk.Basic;

    public List<PortalPermission> Permissions { get; set; } = [];

    public ChangeMode Changes { get; set; } = ChangeMode.NeedsApproval;

    /// <summary>Action and command groups the role may see and touch; empty means all groups.</summary>
    public List<string> Groups { get; set; } = [];
}

/// <summary>A device a member signed in on. The private key never leaves it (WebCrypto non-extractable, Keychain, Keystore).</summary>
public sealed class PortalDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Raw P-256 public key, base64url.</summary>
    public string PublicKey { get; set; } = "";

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the member last signed in with the provider on this device (again every 30 days).</summary>
    public DateTimeOffset SignedInAt { get; set; }
}

public sealed class PortalMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>For the streamer's own list (the provider's display name at sign-in, or what the streamer typed).</summary>
    public string Name { get; set; } = "";

    public IdentityProvider Provider { get; set; }

    /// <summary>The provider's permanent user ID (Google "sub", Twitch user ID, Discord user ID): what sign-in matches.</summary>
    public string ProviderUserId { get; set; } = "";

    public Guid RoleId { get; set; }

    /// <summary>Overrides the role's <see cref="PortalRole.Changes"/> for this member; null follows the role.</summary>
    public ChangeMode? Changes { get; set; }

    /// <summary>Twitch members only: must stay a moderator of the streamer's channel (off by default).</summary>
    public bool RequireTwitchModerator { get; set; }

    /// <summary>Signed-in devices are refused while suspended (by the streamer, or by the Twitch moderator check).</summary>
    public bool Suspended { get; set; }

    public string SuspendedReason { get; set; } = "";

    public DateTimeOffset JoinedAt { get; set; }

    public List<PortalDevice> Devices { get; set; } = [];
}

/// <summary>Optional: the account an invite must be used with.</summary>
public sealed class InviteLock
{
    public IdentityProvider Provider { get; set; }

    /// <summary>A Twitch login or a Google email (compared without regard to case).</summary>
    public string Value { get; set; } = "";
}

/// <summary>A one-time invite. Only a hash of its secret is stored; the link is shown once when it is made.</summary>
public sealed class PortalInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public Guid RoleId { get; set; }

    /// <summary>SHA-256 of the invite secret, base64url.</summary>
    public string SecretHash { get; set; } = "";

    public InviteLock? Lock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>portal.json: the mod portal's identity key, roles, members and open invites.</summary>
public sealed class PortalSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Let mods connect through the shared relay (relay.techdaddydigital.com).</summary>
    public bool Enabled { get; set; }

    /// <summary>Receive webhooks through the shared relay (hooks.techdaddydigital.com), with or without mods.</summary>
    public bool SharedWebhooks { get; set; }

    /// <summary>The path segment that makes this Cuebot's shared webhook addresses unguessable. Secret.</summary>
    [Secret]
    public string RouteSecret { get; set; } = "";

    /// <summary>Another relay address (for testing a deployment, e.g. its workers.dev name); empty for the shared one.</summary>
    public string RelayUrl { get; set; } = "";

    /// <summary>This Cuebot's identity key (PKCS#8, base64). Its loss changes the relay ID. Secret.</summary>
    [Secret]
    public string IdentityKey { get; set; } = "";

    public List<PortalRole> Roles { get; set; } = [];
    public List<PortalMember> Members { get; set; } = [];
    public List<PortalInvite> Invites { get; set; } = [];
}
