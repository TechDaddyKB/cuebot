namespace Cuebot.YouTube;

/// <summary>Google or YouTube rejected a request. <see cref="Reason"/> is Google's machine-readable reason (e.g. "quotaExceeded").</summary>
public sealed class YouTubeApiException(string message, int statusCode = 0, string reason = "") : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Reason { get; } = reason;

    public bool IsQuotaExceeded => Reason is "quotaExceeded" or "dailyLimitExceeded";
    public bool IsRateLimited => Reason is "rateLimitExceeded" or "userRateLimitExceeded" || StatusCode == 429;
    public bool IsChatGone => Reason is "liveChatEnded" or "liveChatNotFound" or "liveChatDisabled";

    /// <summary>The refresh token is no longer valid (revoked, or a Testing-mode project's 7-day expiry): sign in again.</summary>
    public bool NeedsSignIn => Reason is "invalid_grant" or "unauthorized_client" or "signInRequired";
}
