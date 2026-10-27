namespace Cuebot.Integrations;

/// <summary>The app or service refused our credentials; reconnecting will not help until the user fixes them.</summary>
public sealed class IntegrationAuthException(string message) : Exception(message);

/// <summary>A command was sent while the integration is not connected.</summary>
public sealed class IntegrationNotConnectedException(string name) : InvalidOperationException($"{name} is not connected.");

/// <summary>The app answered a request with an error.</summary>
public sealed class IntegrationRequestException(string message, int errorId = 0) : Exception(message)
{
    /// <summary>The app's numeric error id, when it has one (VTube Studio errorID).</summary>
    public int ErrorId { get; } = errorId;
}
