namespace Cuebot.Scripting;

/// <summary>
/// The globals a C# script sees: <c>Args</c>, <c>Cb</c> and <c>CancellationToken</c> are usable directly by name.
/// </summary>
public sealed class CSharpScriptGlobals
{
    internal CSharpScriptGlobals(IDictionary<string, object?> args, CuebotHelpers cb, CancellationToken cancellationToken)
    {
        Args = args;
        Cb = cb;
        CancellationToken = cancellationToken;
    }

    /// <summary>The action run's arguments (case-insensitive). Changes are visible to later sub-actions.</summary>
    public IDictionary<string, object?> Args { get; }

    /// <summary>Cuebot host functions.</summary>
    public CuebotHelpers Cb { get; }

    /// <summary>Signalled when the script times out or the action is cancelled; pass it to long-running calls.</summary>
    public CancellationToken CancellationToken { get; }
}
