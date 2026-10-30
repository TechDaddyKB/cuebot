using Avalonia.Data.Converters;

namespace Cuebot.App.Controls;

/// <summary>Converters over a ConnectionState name (as page view models expose it).</summary>
public static class ConnectionStates
{
    /// <summary>True unless disconnected: there is something to disconnect (connecting, connected or failed).</summary>
    public static readonly FuncValueConverter<string?, bool> IsActive = new(state => state is { Length: > 0 } and not "Disconnected");

    /// <summary>True while disconnected or failed: Connect has something to do.</summary>
    public static readonly FuncValueConverter<string?, bool> CanConnect = new(state => state is null or "Disconnected" or "Error");
}
