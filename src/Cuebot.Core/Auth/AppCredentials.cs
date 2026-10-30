namespace Cuebot.Core.Auth;

/// <summary>
/// The OAuth application Cuebot signs in with on a service: the one built into this release (see
/// <see cref="BuiltInApps"/>) or the user's own, entered under Advanced. Tokens belong to the app that issued them, so
/// a user's own app, once entered, keeps being used until it is cleared.
/// </summary>
public sealed record AppCredentials(string ClientId, string ClientSecret, bool IsBuiltIn)
{
    public static readonly AppCredentials None = new("", "", false);

    public bool IsSet => ClientId.Length > 0;

    /// <summary>The user's own app when its client ID is filled in, otherwise <paramref name="builtIn"/>.</summary>
    public static AppCredentials Choose(string ownClientId, string ownClientSecret, AppCredentials builtIn) =>
        ownClientId.Trim() is { Length: > 0 } id ? new(id, ownClientSecret.Trim(), false) : builtIn;

    // Keeps the secret out of logs and exception messages.
    public override string ToString() => $"AppCredentials {{ ClientId = {ClientId}, IsBuiltIn = {IsBuiltIn} }}";
}

/// <summary>
/// The OAuth apps registered for Cuebot itself, compiled in from the build's environment (CI secrets; see
/// <c>docs/BUILT_IN_APPS.md</c>). A build without them has none, and each service then asks for the user's own app.
/// The secrets of desktop apps cannot be kept secret from someone who inspects the program; the services' consent
/// screens are what protect users.
/// </summary>
public static partial class BuiltInApps
{
    public static AppCredentials Twitch { get; } = Make(TwitchClientId, "");

    /// <summary>Google "Desktop app" client: YouTube and Google Drive browser sign-in.</summary>
    public static AppCredentials Google { get; } = Make(GoogleClientId, GoogleClientSecret);

    /// <summary>Google "TVs and Limited Input devices" client: YouTube sign-in with a code. Optional.</summary>
    public static AppCredentials GoogleDevice { get; } = Make(GoogleDeviceClientId, GoogleDeviceClientSecret);

    public static AppCredentials Kick { get; } = Make(KickClientId, KickClientSecret);

    public static AppCredentials TreatStream { get; } = Make(TreatStreamClientId, TreatStreamClientSecret);

    /// <summary>Every built-in secret, for log scrubbing.</summary>
    public static IEnumerable<string> Secrets =>
        new[] { Google, GoogleDevice, Kick, TreatStream }.Select(a => a.ClientSecret).Where(s => s.Length > 0);

    private static AppCredentials Make(string id, string secret) =>
        id.Trim().Length > 0 ? new(id.Trim(), secret.Trim(), true) : AppCredentials.None;
}
