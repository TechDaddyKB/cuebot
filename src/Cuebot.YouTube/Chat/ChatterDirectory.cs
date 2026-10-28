namespace Cuebot.YouTube.Chat;

/// <summary>
/// Recently seen chatters (display name ↔ channel id) and message authors, so moderation by name and
/// "who wrote the deleted message" work without extra API calls. Bounded and thread-safe.
/// </summary>
internal sealed class ChatterDirectory
{
    private const int MaxEntries = 5000;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _nameById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MessageAuthor> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ProfileUrl, bool IsSponsor)> _details = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageOrder = new();

    public void Remember(string channelId, string displayName)
    {
        if (channelId.Length == 0 || displayName.Length == 0) return;
        lock (_gate)
        {
            if (_nameById.Count >= MaxEntries) { _nameById.Clear(); _idByName.Clear(); _details.Clear(); }
            _nameById[channelId] = displayName;
            _idByName[Normalize(displayName)] = channelId;
        }
    }

    /// <summary>Profile picture and membership of a chatter, for Streamer.bot's gifter* arguments.</summary>
    public void RememberDetails(string channelId, string profileUrl, bool isSponsor)
    {
        if (channelId.Length == 0) return;
        lock (_gate)
        {
            if (_details.Count >= MaxEntries) _details.Clear();
            _details[channelId] = (profileUrl, isSponsor);
        }
    }

    public (string ProfileUrl, bool IsSponsor)? DetailsOf(string channelId)
    {
        lock (_gate) return _details.TryGetValue(channelId, out var d) ? d : null;
    }

    public void RememberMessage(string messageId, string channelId, string displayName, string text)
    {
        if (messageId.Length == 0) return;
        lock (_gate)
        {
            if (_messages.TryAdd(messageId, new MessageAuthor(channelId, displayName, text))) _messageOrder.Enqueue(messageId);
            while (_messageOrder.Count > MaxEntries) _messages.Remove(_messageOrder.Dequeue());
        }
    }

    public string? NameOf(string channelId)
    {
        lock (_gate) return _nameById.GetValueOrDefault(channelId);
    }

    /// <summary>Channel id of a recent chatter by display name (with or without a leading @).</summary>
    public string? IdOf(string displayName)
    {
        lock (_gate) return _idByName.GetValueOrDefault(Normalize(displayName));
    }

    public MessageAuthor? MessageAuthor(string messageId)
    {
        lock (_gate) return _messages.GetValueOrDefault(messageId);
    }

    private static string Normalize(string name) => name.Trim().TrimStart('@');
}

internal sealed record MessageAuthor(string UserId, string UserName, string Text);
