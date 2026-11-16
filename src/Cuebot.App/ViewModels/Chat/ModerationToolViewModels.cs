using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>
/// One moderation tool button for one platform (docs/UNIFIED_MOD_CHAT_PLAN.md, section 4): it works, does the closest
/// thing (and says how it differs), opens the platform's website, or is greyed out with the reason.
/// </summary>
public sealed partial class ModToolViewModel : ObservableObject
{
    private readonly Func<ModToolViewModel, Task> _run;

    public ModToolViewModel(string platform, ModerationCapability capability, Func<ModToolViewModel, Task> run, bool isToggle = false)
    {
        Platform = platform;
        Capability = capability;
        IsToggle = isToggle;
        _run = run;
    }

    public string Platform { get; }
    public ModerationCapability Capability { get; }
    public ModerationAction Action => Capability.Action;
    public ModerationToolState State => Capability.State;

    /// <summary>Chat modes switch on and off; Cuebot remembers the last switch it made (it does not read the mode back yet).</summary>
    public bool IsToggle { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private bool _isOn;

    /// <summary>The button text, with the switch it will make for chat modes and a marker for website tools.</summary>
    public string Label
    {
        get
        {
            var name = ModerationCapability.Label(Action);
            if (IsToggle && Capability.CanRun) name += IsOn ? ": turn off" : ": turn on";
            return State == ModerationToolState.OnWebsite ? name + " (website)" : name;
        }
    }

    /// <summary>Clickable unless the platform has no such tool (website tools open the page).</summary>
    public bool IsEnabled => State != ModerationToolState.NotAvailable;

    public bool IsWebsite => State == ModerationToolState.OnWebsite;

    /// <summary>A word on the state for the tooltip's first line.</summary>
    public string StateText => State switch
    {
        ModerationToolState.Works => "Works",
        ModerationToolState.Closest => "Closest equivalent",
        ModerationToolState.OnWebsite => "On the website",
        _ => "Not available",
    };

    /// <summary>"Kick, closest equivalent: Kick counts whole minutes..." and the YouTube quota cost.</summary>
    public string Hint
    {
        get
        {
            var text = $"{Platform}, {StateText.ToLowerInvariant()}";
            if (Capability.Note.Length > 0) text += ": " + Capability.Note;
            else if (State == ModerationToolState.OnWebsite) text += ": opens the platform's own page.";
            if (Capability.QuotaCost > 0)
                text += string.Format(CultureInfo.CurrentCulture, " Costs {0:N0} units of YouTube's daily quota.", Capability.QuotaCost);
            return text;
        }
    }

    [RelayCommand]
    private Task Run() => _run(this);
}

/// <summary>One mod log row on the chat page.</summary>
public sealed class ModLogLineViewModel(ModLogEntry entry)
{
    public ModLogEntry Entry { get; } = entry;
    public string Platform => Entry.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(entry.Platform);
    public string Time => Entry.At.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"Kris (streamer)", "Sam (mod)", "Shoutout action", "Sam on Twitch's website".</summary>
    public string Actor => Entry.ActorKind switch
    {
        ModActorKind.Streamer => "You",
        ModActorKind.Member => $"{Entry.ActorName} (mod portal)",
        ModActorKind.Automation => $"{Entry.ActorName} (automatic)",
        _ => $"{Entry.ActorName} (on {Entry.Platform})",
    };

    /// <summary>"banned Bob", "timed out Bob for 10 min", "deleted a message of Bob", "slow mode on".</summary>
    public string Summary
    {
        get
        {
            var who = Entry.TargetName.Length > 0 ? Entry.TargetName : Entry.Redacted ? "(removed after the retention period)" : "someone";
            return Entry.Action switch
            {
                "ban" => $"banned {who}",
                "timeout" => $"timed out {who}" + ((Entry.DurationAppliedSeconds ?? Entry.DurationRequestedSeconds) is { } s
                    ? " for " + ModerationService.FormatDuration(TimeSpan.FromSeconds(s)) : ""),
                "unban" => $"lifted the ban of {who}",
                "delete" => $"deleted a message of {who}",
                "warn" => $"warned {who}",
                "clear" => "cleared the chat",
                "mode" => Entry.Reason.Length > 0 ? Entry.Reason : "changed a chat mode",
                _ => $"{Entry.Action} {who}",
            };
        }
    }

    /// <summary>The reason, the deleted message, or why it failed.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Action != "mode" && Entry.Reason.Length > 0) parts.Add("Reason: " + Entry.Reason);
            if (Entry.MessageText.Length > 0) parts.Add("Message: " + Entry.MessageText);
            if (Entry.Result != ModResult.Ok) parts.Add(Entry.Result switch
            {
                ModResult.Failed => "Failed: " + Entry.Error,
                ModResult.Skipped => "Not sent: " + Entry.Error,
                _ => "Done by hand",
            });
            if (Entry.UndoneAt is { } undone) parts.Add($"Lifted {undone.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}");
            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => Detail.Length > 0;
    public bool IsProblem => Entry.Result is ModResult.Failed or ModResult.Skipped;
}
