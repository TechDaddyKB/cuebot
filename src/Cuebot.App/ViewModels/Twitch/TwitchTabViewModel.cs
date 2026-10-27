using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>Shared plumbing of the Twitch page tabs: the service, and an error/info line for the last operation.</summary>
public abstract partial class TwitchTabViewModel(TwitchService twitch) : ObservableObject
{
    protected TwitchService Twitch { get; } = twitch;
    protected TwitchApi Api => Twitch.Api;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsSignedIn => Twitch.IsAuthorized;

    /// <summary>Called on the UI thread when the connection state changes.</summary>
    public virtual void Refresh() => OnPropertyChanged(nameof(IsSignedIn));

    /// <summary>Runs an operation, showing <paramref name="done"/> on success and the exception message on failure.</summary>
    protected async Task Run(Func<Task> operation, string? done = null)
    {
        Error = "";
        Info = "";
        IsBusy = true;
        try
        {
            await operation();
            if (done is not null) Info = done;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
