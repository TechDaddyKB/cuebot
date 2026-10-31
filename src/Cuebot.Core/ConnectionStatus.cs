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

/// <summary>A connection that can be started and stopped on request (the API and MCP tools use it, as the app's pages do).</summary>
public interface IConnectable : IConnectionStatus
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
}
