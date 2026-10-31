using Avalonia.Data.Converters;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>
/// Friendly words for the option lists on the Speech pages. The lists still hold (and save) the setting's own value,
/// like DropNewest; only the text shown in the box changes.
/// </summary>
public static class SpeechChoiceLabels
{
    public static string Label(object? value) => value switch
    {
        BadWordAction.Disabled => "Do nothing",
        BadWordAction.Strip => "Remove the word",
        BadWordAction.Skip => "Skip the message",
        BadWordAction.Replace => "Replace the word",
        UrlMode.Keep => "Read the link",
        UrlMode.Replace => "Say the link word instead",
        UrlMode.Strip => "Remove the link",
        UrlMode.Skip => "Skip the message",
        QueueDropPolicy.DropNewest => "Drop the newest message",
        QueueDropPolicy.DropOldest => "Drop the oldest message",
        SpeakMode.All => "Every message",
        SpeakMode.Command => "Only speaking commands",
        null => "",
        _ => value.ToString() ?? "",
    };

    /// <summary>For a combo box item template: <c>Text="{Binding Converter={x:Static sp:SpeechChoiceLabels.Converter}}"</c>.</summary>
    public static readonly IValueConverter Converter = new FuncValueConverter<object?, string>(Label);
}
