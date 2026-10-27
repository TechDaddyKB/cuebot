namespace Cuebot.YouTube;

/// <summary>Google OAuth and YouTube Data API base addresses; tests point them at a fake server.</summary>
internal sealed record YouTubeEndpoints(Uri OAuth, Uri Authorize, Uri Api)
{
    public static YouTubeEndpoints Production { get; } = new(
        new Uri("https://oauth2.googleapis.com/"),
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://www.googleapis.com/youtube/v3/"));
}
