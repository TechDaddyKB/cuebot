using Cuebot.Core.Actions;
using static Cuebot.Portal.Members.PortalPermission;

namespace Cuebot.Portal.Members;

/// <summary>The roles a new portal starts with (docs/MOD_PORTAL_PLAN.md, section 5). The streamer can change them all.</summary>
public static class DefaultRoles
{
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
            Permissions = [RunActions, CreateCommands, EditCommands, AudioLevels, UploadSounds, ViewActivity],
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
                AudioLevels, StreamControl, ShowCritical, UploadSounds, UploadMedia, EditOverlays, ViewActivity, UndoOthers,
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
