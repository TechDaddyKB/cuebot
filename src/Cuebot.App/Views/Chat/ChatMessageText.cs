using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;

namespace Cuebot.App.Views.Chat;

/// <summary>
/// One chat line as wrapped text: badges, the coloured name, pronouns, then the message with emotes drawn inline as
/// images (their code shows until the image loads, or when it cannot be loaded).
/// </summary>
public sealed class ChatMessageText : TextBlock
{
    public const double EmoteHeight = 28;

    public static readonly StyledProperty<ChatLineViewModel?> LineProperty =
        AvaloniaProperty.Register<ChatMessageText, ChatLineViewModel?>(nameof(Line));

    private ChatLineViewModel? _subscribed;

    public ChatMessageText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public ChatLineViewModel? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LineProperty) return;
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnLineChanged;
        _subscribed = Line;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnLineChanged;
        Rebuild();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatLineViewModel.Segments) or nameof(ChatLineViewModel.Pronouns) or nameof(ChatLineViewModel.IsDeleted))
            Rebuild();
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        if (Line is not { } line) return;

        if (line.Badges.Length > 0) inlines.Add(new Run(line.Badges));
        var name = new Run(line.User) { FontWeight = FontWeight.SemiBold };
        if (line.UserBrush is { } brush) name.Foreground = brush;
        inlines.Add(name);
        if (line.HasPronouns)
            inlines.Add(new Run($" ({line.Pronouns})") { FontSize = Math.Max(9, FontSize - 2), Classes = { "pronouns" }, Foreground = Muted() });
        if (line.HasAmount) inlines.Add(new Run($" [{line.Amount}]") { FontWeight = FontWeight.SemiBold });
        inlines.Add(new Run(": "));

        foreach (var segment in line.Segments)
        {
            switch (segment)
            {
                case EmoteSegment emote:
                    inlines.Add(new InlineUIContainer(CreateEmote(emote)) { BaselineAlignment = BaselineAlignment.Center });
                    break;
                default:
                    var run = new Run(segment.Text);
                    if (line.IsDeleted) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                    inlines.Add(run);
                    break;
            }
        }
        if (line.IsDeleted) inlines.Add(new Run("  (deleted)") { Foreground = Muted() });
    }

    private IBrush? Muted() =>
        this.TryFindResource("CuebotMuted", ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    /// <summary>An emote image (with zero-width overlays stacked on it) and its text fallback.</summary>
    public static Control CreateEmote(EmoteSegment emote)
    {
        var panel = new Grid { Tag = emote };
        ToolTip.SetTip(panel, emote.ToolTip);
        panel.Children.Add(Image(emote));
        foreach (var overlay in emote.Overlays) panel.Children.Add(Image(overlay));
        var fallback = new TextBlock { Text = emote.Text, VerticalAlignment = VerticalAlignment.Center, Classes = { "emoteFallback" } };
        fallback.Bind(IsVisibleProperty, new Binding(nameof(EmoteSegment.HasImage)) { Source = emote, Converter = Avalonia.Data.Converters.BoolConverters.Not });
        panel.Children.Add(fallback);
        return panel;
    }

    private static Image Image(EmoteSegment emote)
    {
        var image = new Image
        {
            Height = EmoteHeight,
            MaxWidth = EmoteHeight * 4,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "emote" },
        };
        image.Bind(Avalonia.Controls.Image.SourceProperty, new Binding(nameof(EmoteSegment.Image)) { Source = emote });
        image.Bind(IsVisibleProperty, new Binding(nameof(EmoteSegment.HasImage)) { Source = emote });
        return image;
    }
}
