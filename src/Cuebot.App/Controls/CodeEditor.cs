using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Cuebot.App.Controls;

/// <summary>Syntax-highlighted code editor (AvaloniaEdit + TextMate) with a bindable Text property.</summary>
public sealed class CodeEditor : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Language));

    private static readonly RegistryOptions Grammars = new(ThemeName.DarkPlus);

    private readonly TextEditor _editor;
    private readonly TextMate.Installation _textMate;
    private bool _syncing;

    public CodeEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Code,Consolas,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _textMate = _editor.InstallTextMate(Grammars);
        _editor.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            SetCurrentValue(TextProperty, _editor.Text);
            _syncing = false;
        };
        Content = _editor;
        ApplyLanguage();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
        {
            _syncing = true;
            _editor.Text = Text ?? "";
            _syncing = false;
        }
        else if (change.Property == LanguageProperty)
        {
            ApplyLanguage();
        }
    }

    private void ApplyLanguage()
    {
        var extension = Language == "python" ? ".py" : ".cs";
        var language = Grammars.GetLanguageByExtension(extension);
        if (language is not null) _textMate.SetGrammar(Grammars.GetScopeByLanguageId(language.Id));
    }
}
