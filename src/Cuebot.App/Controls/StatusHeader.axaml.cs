using Avalonia;
using Avalonia.Controls;

namespace Cuebot.App.Controls;

/// <summary>
/// The header of a platform or service page: a brand tile, the name, a state chip (a ConnectionState name, with a green or red dot), a detail line
/// (account or message) and the page's own buttons on the right.
/// </summary>
public partial class StatusHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<StatusHeader, string?>(nameof(Title));

    /// <summary>Picks the tile colour and initial (see <see cref="PlatformBrush"/>).</summary>
    public static readonly StyledProperty<string?> BrandProperty = AvaloniaProperty.Register<StatusHeader, string?>(nameof(Brand));

    public static readonly StyledProperty<string?> StateProperty = AvaloniaProperty.Register<StatusHeader, string?>(nameof(State));

    public static readonly StyledProperty<string?> DetailProperty = AvaloniaProperty.Register<StatusHeader, string?>(nameof(Detail));

    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<StatusHeader, object?>(nameof(Actions));

    public StatusHeader() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StateProperty) return;
        // The state is a ConnectionState name: the dot is green when connected, red on an error.
        StateDot.Classes.Set("good", State == "Connected");
        StateDot.Classes.Set("bad", State == "Error");
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Brand { get => GetValue(BrandProperty); set => SetValue(BrandProperty, value); }
    public string? State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}
