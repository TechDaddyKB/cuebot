using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The "click again to confirm" step for buttons that are hard to undo or that viewers see (ban, clear chat, going live):
/// the first click only arms the button, whose <see cref="Text"/> then asks for a second click; a second click within
/// 5 seconds goes ahead. After 5 seconds the button turns back by itself. Same idea as deleting an action with the
/// Delete key on the Actions page.
/// </summary>
public sealed partial class ConfirmClick(string text, string armedText) : ObservableObject
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private string? _armedFor;
    private DateTime _armedAt;
    private int _arms;

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _isArmed;

    /// <summary>The button text: the normal text, or the "click again" text while armed.</summary>
    public string Text => IsArmed ? armedText : text;

    /// <summary>
    /// True when the click should go ahead: the second click within 5 seconds for the same <paramref name="key"/>
    /// (for example the same user name). Otherwise arms the button and returns false.
    /// </summary>
    public bool Confirm(string key = "")
    {
        var now = Clock();
        if (IsArmed && _armedFor == key && now - _armedAt < Window)
        {
            Reset();
            return true;
        }
        _armedFor = key;
        _armedAt = now;
        IsArmed = true;
        var arm = ++_arms;
        DispatcherTimer.RunOnce(() => { if (arm == _arms) Reset(); }, Window);
        return false;
    }

    /// <summary>Turns the button back to normal without doing anything.</summary>
    public void Reset()
    {
        _armedFor = null;
        IsArmed = false;
    }
}
