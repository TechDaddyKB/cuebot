using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Chat.Emotes;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>A piece of a rendered chat message: plain text or an emote.</summary>
public abstract class ChatSegment : ObservableObject
{
    public abstract string Text { get; }
}

public sealed class TextSegment(string text) : ChatSegment
{
    public override string Text { get; } = text;
}

/// <summary>An emote drawn as an image once it loads (its code shows until then, or if it never does).</summary>
public partial class EmoteSegment : ChatSegment
{
    public EmoteSegment(EmoteMatch match)
    {
        Match = match;
        Text = match.Text;
    }

    public EmoteMatch Match { get; }
    public override string Text { get; }
    public string Code => Match.Code;
    public string Provider => Match.Provider;
    public string ImageUrl => Match.ImageUrl;
    public string ToolTip => $"{Match.Code} ({Match.Provider})";

    /// <summary>Zero-width emotes drawn on top of this one.</summary>
    public List<EmoteSegment> Overlays { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    /// <summary>Starts loading this emote's image (and its overlays').</summary>
    public void Load(EmoteImageLoader? loader, Action<Action> onUi)
    {
        if (loader is null) return;
        foreach (var overlay in Overlays) overlay.Load(loader, onUi);
        if (loader.TryGet(ImageUrl) is { } ready)
        {
            Image = ready;
            return;
        }
        _ = loader.LoadAsync(ImageUrl).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } bitmap) onUi(() => Image = bitmap);
        }, TaskScheduler.Default);
    }
}

public static class ChatSegmenter
{
    /// <summary>
    /// Builds segments for a message. Zero-width emotes that follow another emote (whitespace between) become overlays
    /// of that emote rather than separate images.
    /// </summary>
    public static IReadOnlyList<ChatSegment> Build(string text, IReadOnlyList<EmoteMatch> matches)
    {
        if (matches.Count == 0) return [new TextSegment(text)];
        var segments = new List<ChatSegment>();
        foreach (var (piece, match) in EmoteParser.Split(text, matches))
        {
            if (match is null)
            {
                segments.Add(new TextSegment(piece));
                continue;
            }
            var emote = new EmoteSegment(match);
            if (match.Emote.IsZeroWidth && LastEmote(segments) is { } host)
            {
                if (segments[^1] is TextSegment { Text: var gap } && string.IsNullOrWhiteSpace(gap)) segments.RemoveAt(segments.Count - 1);
                host.Overlays.Add(emote);
                continue;
            }
            segments.Add(emote);
        }
        return segments;
    }

    private static EmoteSegment? LastEmote(List<ChatSegment> segments) =>
        segments.Count switch
        {
            0 => null,
            _ when segments[^1] is EmoteSegment e => e,
            > 1 when segments[^1] is TextSegment t && string.IsNullOrWhiteSpace(t.Text) && segments[^2] is EmoteSegment e => e,
            _ => null,
        };
}
