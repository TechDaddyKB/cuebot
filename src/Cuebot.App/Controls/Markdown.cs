using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cuebot.App.Controls;

public enum MdBlockKind { Paragraph, Heading, Bullet, Numbered, Code, Quote, Rule }

/// <summary>A block of an AI answer. <see cref="Level"/> is the heading level, list depth or item number.</summary>
public sealed record MdBlock(MdBlockKind Kind, string Text, int Level = 0);

[Flags]
public enum MdStyle { None = 0, Bold = 1, Italic = 2, Code = 4 }

/// <summary>A run of inline text; <see cref="Link"/> is set for links (http/https only).</summary>
public sealed record MdSpan(string Text, MdStyle Style, string? Link = null);

/// <summary>
/// The Markdown the assistant's AIs write: headings, bullet and numbered lists, fenced code, quotes, rules, tables (kept as
/// monospace text) and paragraphs; inline bold, italic, code and links. Anything else stays plain text.
/// </summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MdBlock> Blocks(string markdown)
    {
        var blocks = new List<MdBlock>();
        var paragraph = new StringBuilder();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        void Flush()
        {
            if (paragraph.Length > 0) blocks.Add(new MdBlock(MdBlockKind.Paragraph, paragraph.ToString().Trim()));
            paragraph.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.AppendLine(lines[i]);
                blocks.Add(new MdBlock(MdBlockKind.Code, code.ToString().TrimEnd('\n', '\r')));
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                // A table: kept as monospace text, without the |---| separator line.
                Flush();
                var table = new StringBuilder();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    if (!TableRule().IsMatch(lines[i].Trim())) table.AppendLine(lines[i].Trim());
                i--;
                blocks.Add(new MdBlock(MdBlockKind.Code, table.ToString().TrimEnd()));
                continue;
            }
            if (Heading().Match(trimmed) is { Success: true } h)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Heading, h.Groups[2].Value.Trim(), h.Groups[1].Length));
                continue;
            }
            if (Rule().IsMatch(trimmed))
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Rule, ""));
                continue;
            }
            var indent = (line.Length - trimmed.Length) / 2;
            if (Bullet().Match(trimmed) is { Success: true } b)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, b.Groups[1].Value, indent));
                continue;
            }
            if (Numbered().Match(trimmed) is { Success: true } n)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Numbered, n.Groups[2].Value, int.Parse(n.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Quote, trimmed.TrimStart('>').Trim()));
                continue;
            }
            // A list item's continuation line joins the item.
            if (paragraph.Length == 0 && blocks.Count > 0 && blocks[^1].Kind is MdBlockKind.Bullet or MdBlockKind.Numbered && line.StartsWith("  ", StringComparison.Ordinal))
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + trimmed };
                continue;
            }
            paragraph.Append(paragraph.Length > 0 ? " " : "").Append(trimmed);
        }
        Flush();
        return blocks;
    }

    public static IReadOnlyList<MdSpan> Spans(string text)
    {
        var spans = new List<MdSpan>();
        var position = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > position) spans.Add(new MdSpan(text[position..m.Index], MdStyle.None));
            if (m.Groups["code"].Success) spans.Add(new MdSpan(m.Groups["code"].Value, MdStyle.Code));
            else if (m.Groups["link"].Success)
            {
                var url = m.Groups["url"].Value;
                var safe = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
                spans.Add(new MdSpan(m.Groups["link"].Value, MdStyle.None, safe ? uri!.AbsoluteUri : null));
            }
            else if (m.Groups["bold"].Success) spans.AddRange(Spans(m.Groups["bold"].Value).Select(s => s with { Style = s.Style | MdStyle.Bold }));
            else if (m.Groups["italic"].Success) spans.AddRange(Spans(m.Groups["italic"].Value).Select(s => s with { Style = s.Style | MdStyle.Italic }));
            position = m.Index + m.Length;
        }
        if (position < text.Length) spans.Add(new MdSpan(text[position..], MdStyle.None));
        return spans;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d{1,4})[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\[(?<link>[^\]]+)\]\((?<url>[^)\s]+)\)|\*\*(?<bold>.+?)\*\*(?!\*)|__(?<bold>.+?)__(?!_)|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<![\w_])_(?<italic>[^_\s][^_]*?)_(?![\w_])")]
    private static partial Regex InlineToken();
}

/// <summary>Shows an AI answer's Markdown (see <see cref="MarkdownParser"/>), selectable; links open in the browser.</summary>
public sealed class MarkdownText : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    /// <summary>Opens a link (tests replace it).</summary>
    public static Action<string> OpenLink { get; set; } = Cuebot.App.Shell.UrlLauncher.Open;

    public MarkdownText() => Spacing = 6;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in MarkdownParser.Blocks(Markdown ?? ""))
            Children.Add(block.Kind switch
            {
                MdBlockKind.Code => Themed(Themed(new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6),
                    Child = new SelectableTextBlock { Text = block.Text, FontFamily = new FontFamily("monospace"), FontSize = 13, TextWrapping = TextWrapping.Wrap },
                }, Border.BackgroundProperty, "CuebotSidebar"), Border.BorderBrushProperty, "CuebotCardBorder"),
                MdBlockKind.Rule => Themed(new Border { Height = 1, Margin = new Thickness(0, 4) }, Border.BackgroundProperty, "CuebotCardBorder"),
                MdBlockKind.Bullet => Item("•", block.Text, block.Level),
                MdBlockKind.Numbered => Item(block.Level + ".", block.Text, 0),
                MdBlockKind.Quote => Themed(new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Child = Paragraph(block.Text, FontStyle.Italic),
                }, Border.BorderBrushProperty, "CuebotMuted"),
                MdBlockKind.Heading => Heading(block),
                _ => Paragraph(block.Text),
            });
    }

    private Control Heading(MdBlock block)
    {
        var text = Paragraph(block.Text);
        text.FontWeight = FontWeight.SemiBold;
        text.FontSize = block.Level switch { 1 => 20, 2 => 17, _ => 15 };
        return text;
    }

    private Control Item(string marker, string text, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(depth * 16, 0, 0, 0) };
        var bullet = new TextBlock { Text = marker, Margin = new Thickness(0, 0, 8, 0), MinWidth = 14 };
        var body = Paragraph(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(body);
        return grid;
    }

    private SelectableTextBlock Paragraph(string text, FontStyle style = FontStyle.Normal)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontStyle = style, FontSize = 14, Inlines = [] };
        foreach (var span in MarkdownParser.Spans(text))
        {
            if (span.Link is { } url)
            {
                var link = Themed(new TextBlock
                {
                    Text = span.Text,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = new Cursor(StandardCursorType.Hand),
                }, TextBlock.ForegroundProperty, "CuebotAccent");
                ToolTip.SetTip(link, url);
                link.PointerPressed += (_, e) =>
                {
                    OpenLink(url);
                    e.Handled = true;
                };
                block.Inlines!.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
                continue;
            }
            var run = new Run(span.Text);
            if (span.Style.HasFlag(MdStyle.Bold)) run.FontWeight = FontWeight.SemiBold;
            if (span.Style.HasFlag(MdStyle.Italic)) run.FontStyle = FontStyle.Italic;
            if (span.Style.HasFlag(MdStyle.Code))
            {
                run.FontFamily = new FontFamily("monospace");
                Themed(run, TextElement.BackgroundProperty, "CuebotSidebar");
            }
            block.Inlines!.Add(run);
        }
        return block;
    }

    /// <summary>Follows the theme resource (also once the control is in the window, and on light/dark switches).</summary>
    private static T Themed<T>(T element, AvaloniaProperty property, string key) where T : StyledElement
    {
        element.Bind(property, element.GetResourceObservable(key));
        return element;
    }
}
