using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App.Controls;

namespace Cuebot.App.Views;

/// <summary>
/// A page's keyboard shortcuts (after Streamer.bot's documented per-view shortcuts, listed in
/// <see cref="Cuebot.App.Shell.AppShortcuts"/>): a tunnel key handler on part of the page, so the lists do not swallow
/// Delete, Home or End first. Keys typed into a text field are left to the field. Ctrl and Cmd count the same.
/// </summary>
internal static class PageKeys
{
    /// <param name="handler">Returns true when it handled the key.</param>
    /// <param name="selected">
    /// The item to keep the keyboard on afterwards (lists rebuild their rows after a move or toggle, which drops the
    /// focus); by default the item the key was pressed on.
    /// </param>
    public static void Handle(Control scope, Func<KeyEventArgs, bool> handler, Func<object?>? selected = null) =>
        scope.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || IsTextInput(e.Source)) return;
            var before = (e.Source as StyledElement)?.DataContext;
            if (!handler(e)) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => Refocus(scope, selected is null ? before : selected(), follow: selected is not null),
                DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);

    /// <summary>
    /// Puts the keyboard back on the item (its list row first), or on the scope when the item is gone or hidden. With
    /// <paramref name="follow"/> the keyboard also moves to a newly selected item (after add or duplicate).
    /// </summary>
    private static void Refocus(Control scope, object? item, bool follow)
    {
        var focused = scope.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.IsFocused);
        if (focused is not null && (!follow || item is null || (focused as StyledElement)?.DataContext == item)) return;
        var candidates = item is null
            ? []
            : scope.GetVisualDescendants().OfType<Control>()
                .Where(c => c.DataContext == item && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && !IsTextInput(c))
                .ToList();
        var target = candidates.FirstOrDefault(c => c is ListBoxItem) ?? candidates.FirstOrDefault();
        if (target is not null) target.Focus();
        else if (scope.Focusable) scope.Focus();
    }

    /// <summary>Ctrl (or Cmd) without Alt; Shift is allowed (Ctrl+* is Ctrl+Shift+8 on many layouts).</summary>
    public static bool Ctrl(KeyEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && (e.KeyModifiers & KeyModifiers.Alt) == 0;

    public static bool Plain(KeyEventArgs e) => e.KeyModifiers == KeyModifiers.None;

    /// <summary>The item the key was pressed on (the data context of the focused element), when it is a <typeparamref name="T"/>.</summary>
    public static T? Item<T>(KeyEventArgs e) where T : class => (e.Source as StyledElement)?.DataContext as T;

    /// <summary>Runs <paramref name="command"/> when it can run; true when it ran.</summary>
    public static bool Run(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter)) return false;
        command.Execute(parameter);
        return true;
    }

    /// <summary>True when the key goes to a text field (its own editing keys apply).</summary>
    public static bool IsTextInput(object? source) =>
        source is Visual v && (v.FindAncestorOfType<TextBox>(includeSelf: true) is not null
                               || v.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not null
                               || v.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not null
                               || v.FindAncestorOfType<CodeEditor>(includeSelf: true) is not null);
}
