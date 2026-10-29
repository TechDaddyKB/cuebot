namespace Cuebot.Assistant;

/// <summary>One saved line of the Assistant page's conversation (assistant-history.json).</summary>
public sealed class AssistantHistoryEntry
{
    /// <summary>User, Assistant, Tool, Confirmation, Error or Notice.</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>
/// assistant-history.json: the last conversation as the page showed it, so it is still there after a restart. The AI
/// does not get it back; it is for the streamer to read.
/// </summary>
public sealed class AssistantHistoryFile
{
    public const int MaxEntries = 200;

    public List<AssistantHistoryEntry> Entries { get; set; } = [];
}
