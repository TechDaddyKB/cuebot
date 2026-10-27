namespace Cuebot.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Error }

/// <summary>Implemented by every platform connection so the UI can show status generically.</summary>
public interface IConnectionStatus
{
    string Name { get; }
    ConnectionState State { get; }
    string StatusMessage { get; }
    event Action? StatusChanged;
}
