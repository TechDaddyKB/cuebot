using Cuebot.Core.Actions;
using static Cuebot.Portal.Members.PortalPermission;

namespace Cuebot.Portal.Members;

/// <summary>
/// The roles a new portal starts with (docs/design/MOD_PORTAL_PLAN.md, section 5; chat rights from docs/design/UNIFIED_MOD_CHAT_PLAN.md,
/// section 8 and decisions 4 and 10). The streamer can change them all.
/// </summary>
public static class DefaultRoles
{
    /// <summary>
    /// The combined chat for a Standard Mod: see it, delete, time out, ban and unban, the whole mod log, read and add
    /// notes, propose links, linked timeouts and bans. Not chat modes, blocked terms, deleting notes or confirming links.
    /// </summary>
    public static IReadOnlyList<PortalPermission> StandardChat { get; } =
        [ViewChat, ChatDeleteTimeout, ChatBan, ViewModLog, NotesRead, NotesAdd, ProposeLinks, LinkedTimeouts, LinkedBans];

    /// <summary>Every chat right (Lead Mod and Tech Lead).</summary>
    public static IReadOnlyList<PortalPermission> LeadChat { get; } =
        [ViewChat, ChatDeleteTimeout, ChatBan, ChatModes, BlockedTerms, ViewModLog, NotesRead, NotesAdd, NotesDelete,
         ProposeLinks, ConfirmLinks, LinkedTimeouts, LinkedBans];

    /// <summary>What a role the streamer made before chat existed gets: it may see the chat and the mod log, not act.</summary>
    public static IReadOnlyList<PortalPermission> CustomRoleChat { get; } = [ViewChat, ViewModLog];

    /// <summary>
    /// Adds the chat rights to a role saved before they existed (portal.json version 1): the defaults above for the
    /// three built-in roles; every right for a custom role that could already ban and end the stream (show-critical);
    /// only seeing the chat and the log for other custom roles. Rights the role already has are kept.
    /// </summary>
    public static void AddChatPermissions(PortalRole role)
    {
        IReadOnlyList<PortalPermission> add = role.Id == StandardModId ? StandardChat
            : role.Id == LeadModId || role.Id == TechLeadId || role.Permissions.Contains(ShowCritical) ? LeadChat
            : CustomRoleChat;
        role.Permissions = [.. role.Permissions.Concat(add).Distinct()];
    }

    public static readonly Guid StandardModId = new("6b0f5c1e-4d38-4c4e-9a43-2d1f0c7a5b01");
    public static readonly Guid LeadModId = new("6b0f5c1e-4d38-4c4e-9a43-2d1f0c7a5b02");
    public static readonly Guid TechLeadId = new("6b0f5c1e-4d38-4c4e-9a43-2d1f0c7a5b03");

    public static IReadOnlyList<PortalRole> Create() =>
    [
        new()
        {
            Id = StandardModId,
            Name = "Standard Mod",
            MaxRisk = StepRisk.Basic,
            Changes = ChangeMode.NeedsApproval,
            Permissions = [RunActions, CreateCommands, EditCommands, AudioLevels, UploadSounds, ViewActivity, .. StandardChat],
        },
        new()
        {
            Id = LeadModId,
            Name = "Lead Mod",
            MaxRisk = StepRisk.ShowCritical,
            Changes = ChangeMode.LiveAndNotify,
            Permissions =
            [
                RunActions, CreateCommands, EditCommands, DeleteCommands, CreateActions, EditActions, DeleteActions,
                AudioLevels, StreamControl, ShowCritical, UploadSounds, UploadMedia, EditOverlays, ViewActivity, UndoOthers, UseAssistant, ManageRewards, .. LeadChat,
            ],
        },
        new()
        {
            Id = TechLeadId,
            Name = "Tech Lead",
            MaxRisk = StepRisk.TrustedTech,
            Changes = ChangeMode.Live,
            Permissions = [.. Enum.GetValues<PortalPermission>()],
        },
    ];
}
