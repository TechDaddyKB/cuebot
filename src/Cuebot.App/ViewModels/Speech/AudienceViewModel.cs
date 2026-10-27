using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>Check boxes over a <see cref="ChatAudience"/> value that is read and written through delegates.</summary>
public sealed class AudienceViewModel(Func<ChatAudience> get, Action<ChatAudience> set) : ObservableObject
{
    public ChatAudience Value
    {
        get => get();
        set
        {
            if (get() == value) return;
            set(value);
            Refresh();
        }
    }

    public bool Everyone { get => Has(ChatAudience.Everyone); set => Toggle(ChatAudience.Everyone, value); }
    public bool Regulars { get => Has(ChatAudience.Regulars); set => Toggle(ChatAudience.Regulars, value); }
    public bool Subscribers { get => Has(ChatAudience.Subscribers); set => Toggle(ChatAudience.Subscribers, value); }
    public bool Vips { get => Has(ChatAudience.Vips); set => Toggle(ChatAudience.Vips, value); }
    public bool Moderators { get => Has(ChatAudience.Moderators); set => Toggle(ChatAudience.Moderators, value); }

    /// <summary>The narrower boxes only matter while "Everyone" is off.</summary>
    public bool IsRestricted => !Everyone;

    public string Summary => Describe(Value);

    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string Describe(ChatAudience audience)
    {
        if (audience.HasFlag(ChatAudience.Everyone)) return "Everyone";
        var parts = new List<string>();
        if (audience.HasFlag(ChatAudience.Moderators)) parts.Add("Moderators");
        if (audience.HasFlag(ChatAudience.Vips)) parts.Add("VIPs");
        if (audience.HasFlag(ChatAudience.Subscribers)) parts.Add("Subscribers");
        if (audience.HasFlag(ChatAudience.Regulars)) parts.Add("Regulars");
        return parts.Count == 0 ? "Broadcaster only" : string.Join(", ", parts);
    }

    private bool Has(ChatAudience flag) => get().HasFlag(flag);

    private void Toggle(ChatAudience flag, bool on) => Value = on ? get() | flag : get() & ~flag;
}
