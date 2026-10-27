namespace Cuebot.Rumble.Auth;

/// <summary>A signed-in Rumble web session: the value of the <c>u_s</c> cookie and the account it belongs to.</summary>
internal sealed record RumbleSession(string Cookie, string UserName)
{
    public const string CookieName = "u_s";
}
