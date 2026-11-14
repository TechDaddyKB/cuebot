using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace Cuebot.App.Controls;

/// <summary>
/// A colour setting: a swatch dropdown with a palette (and "Default", which clears it) next to a text box for any
/// other colour code or name. The text box is marked when its text is not a colour, so a typing mistake shows at once.
/// <see cref="Value"/> is the text as stored ("" = default).
/// </summary>
public partial class ColorField : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<ColorField, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderProperty = AvaloniaProperty.Register<ColorField, string?>(nameof(Placeholder));

    /// <summary>The palette: Cuebot's button colours first, then light, mid and dark tones.</summary>
    internal static readonly string[] Colors =
    [
        "#3b82f6", "#9333ea", "#ef4444", "#22c55e", "#f59e0b", "#ec4899", "#14b8a6", "#6b7280",
        "#93c5fd", "#d8b4fe", "#fca5a5", "#86efac", "#fcd34d", "#f9a8d4", "#5eead4", "#e5e7eb",
        "#1e3a8a", "#581c87", "#7f1d1d", "#14532d", "#78350f", "#831843", "#134e4a", "#16161c",
        "#ffffff", "#000000", "#53fc18", "#9146ff", "#ff0000", "#00b2ff", "#fe2c55", "#5865f2",
    ];

    private bool _updating;

    public ColorField()
    {
        InitializeComponent();
        foreach (var hex in Colors)
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse(hex)), BorderBrush = new SolidColorBrush(Color.Parse("#80808080")),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, hex);
            Avalonia.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) => Choose(hex);
            Palette.Children.Add(swatch);
        }
        DefaultButton.Click += (_, _) => Choose("");
        Input.TextChanged += (_, _) =>
        {
            if (_updating) return;
            Value = Input.Text ?? "";
        };
        Show();
    }

    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private void Choose(string hex)
    {
        Value = hex;
        PickButton.Flyout?.Hide();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) Show();
        else if (change.Property == PlaceholderProperty) Input.PlaceholderText = Placeholder;
    }

    /// <summary>A colour code (#rgb, #rrggbb, #aarrggbb) or a colour name; empty counts as valid (the default).</summary>
    internal static bool IsColor(string? text) => string.IsNullOrWhiteSpace(text) || Color.TryParse(text.Trim(), out _);

    private void Show()
    {
        var text = Value ?? "";
        if (Input.Text != text)
        {
            _updating = true;
            Input.Text = text;
            _updating = false;
        }
        var valid = IsColor(text);
        Input.Classes.Set("error", !valid);
        ToolTip.SetTip(Input, valid ? null : "Not a colour: use a code like #3b82f6 or a name like red, or pick one from the dropdown.");
        Swatch.Background = valid && !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out var c)
            ? new SolidColorBrush(c)
            : null;
    }
}
