using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Cuebot.App.Controls;

namespace Cuebot.UiTests;

public sealed class MarkdownTests
{
    [Fact]
    public void Blocks_cover_what_AI_answers_use()
    {
        var blocks = MarkdownParser.Blocks("""
            ## Your actions

            You have **two** actions:
            - Hello
              (says hi)
            - Raid alert
            1. Open *Actions*
            2. Pick one
            > Tip: test first
            ```
            !lurk
            ```
            | Name | Enabled |
            |------|---------|
            | Hello | yes |
            ---
            Done.
            """);

        Assert.Equal(
            [
                (MdBlockKind.Heading, "Your actions", 2), (MdBlockKind.Paragraph, "You have **two** actions:", 0),
                (MdBlockKind.Bullet, "Hello (says hi)", 0), (MdBlockKind.Bullet, "Raid alert", 0),
                (MdBlockKind.Numbered, "Open *Actions*", 1), (MdBlockKind.Numbered, "Pick one", 2), (MdBlockKind.Quote, "Tip: test first", 0),
                (MdBlockKind.Code, "!lurk", 0), (MdBlockKind.Code, "| Name | Enabled |\n| Hello | yes |", 0), (MdBlockKind.Rule, "", 0),
                (MdBlockKind.Paragraph, "Done.", 0),
            ],
            blocks.Select(b => (b.Kind, b.Text.Replace("\r\n", "\n"), b.Level)));
    }

    [Fact]
    public void Inline_styles_nest_and_only_web_links_open()
    {
        var spans = MarkdownParser.Spans("Use `!lurk`, **very *important***, see [docs](https://cuebot.example/docs) or [this](javascript:alert(1)) and snake_case_name.");

        Assert.Contains(spans, s => s is { Text: "!lurk", Style: MdStyle.Code });
        Assert.Contains(spans, s => s is { Text: "very ", Style: MdStyle.Bold });
        Assert.Contains(spans, s => s is { Text: "important", Style: MdStyle.Bold | MdStyle.Italic });
        Assert.Contains(spans, s => s is { Text: "docs", Link: "https://cuebot.example/docs" });
        Assert.Contains(spans, s => s is { Text: "this", Link: null });
        Assert.EndsWith("snake_case_name.", spans[^1].Text);   // underscores inside words are not italics
    }

    [AvaloniaFact]
    public void The_control_renders_blocks_and_styles()
    {
        var view = new MarkdownText { Markdown = "# Title\n\nSome **bold** text\n\n- one\n- two\n\n```\ncode\n```" };
        var window = new Window { Content = view };
        window.Show();

        Assert.Equal(5, view.Children.Count);
        var paragraph = Assert.IsType<SelectableTextBlock>(view.Children[1]);
        Assert.Contains(paragraph.Inlines!.OfType<Run>(), r => r.Text == "bold" && r.FontWeight == FontWeight.SemiBold);
        Assert.Equal("code", Assert.IsType<SelectableTextBlock>(Assert.IsType<Border>(view.Children[4]).Child).Text);

        view.Markdown = "changed";
        Assert.Single(view.Children);
    }
}
