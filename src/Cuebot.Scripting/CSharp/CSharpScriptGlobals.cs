using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting;

/// <summary>
/// The globals a C# script sees: <c>Args</c>, <c>Cb</c>, <c>CPH</c> and <c>CancellationToken</c> are usable directly
/// by name (<c>args</c> is an alias of <c>Args</c> for code pasted from Streamer.bot).
/// </summary>
public sealed class CSharpScriptGlobals
{
    internal CSharpScriptGlobals(IDictionary<string, object?> args, CuebotHelpers cb, CancellationToken cancellationToken,
        CphProxy? cph = null)
    {
        Args = args;
        Cb = cb;
        CancellationToken = cancellationToken;
        CPH = cph!;
    }

    /// <summary>The action run's arguments (case-insensitive). Changes are visible to later sub-actions.</summary>
    public IDictionary<string, object?> Args { get; }

    /// <summary>Same as <see cref="Args"/>, under Streamer.bot's name.</summary>
#pragma warning disable IDE1006 // Streamer.bot's spelling
    public IDictionary<string, object?> args => Args;
#pragma warning restore IDE1006

    /// <summary>Cuebot host functions.</summary>
    public CuebotHelpers Cb { get; }

    /// <summary>Streamer.bot-compatible host methods (synchronous, Streamer.bot names).</summary>
    public CphProxy CPH { get; }

    /// <summary>Signalled when the script times out or the action is cancelled; pass it to long-running calls.</summary>
    public CancellationToken CancellationToken { get; }
}
