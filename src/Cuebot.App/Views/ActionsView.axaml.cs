using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cuebot.App.Controls;
using Cuebot.App.ViewModels.Actions;

namespace Cuebot.App.Views;

/// <summary>
/// Actions page. Editor shortcuts (outside text fields, where the field's own clipboard and undo apply):
/// Ctrl+C copy (the action when the action list has focus, else the selected trigger/sub-action), Ctrl+V paste,
/// Ctrl+D duplicate, Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo, Delete removes the selected trigger/sub-action (in the
/// action list: the action, on a second press), Ctrl+O turns the selection on/off, Ctrl+Up/Down move a sub-action, Ctrl+Home/End move it to the top/bottom, Ctrl+R
/// renames the selected group.
/// F5 (test trigger) is a window shortcut. The list is <see cref="Cuebot.App.Shell.AppShortcuts.Actions"/>.
/// </summary>
public partial class ActionsView : UserControl
{
    public ActionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ActionsViewModel vm)
            {
                vm.Picker.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TypePickerViewModel.IsOpen) && vm.Picker.IsOpen) PickerSearch.Focus();
                };
                ConnectClipboard(vm);
            }
        };
        // Right-click selects what is under the pointer first, so the context menu acts on it.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private ActionsViewModel? ViewModel => DataContext as ActionsViewModel;

    private void ConnectClipboard(ActionsViewModel vm)
    {
        vm.WriteSystemClipboard = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        vm.ReadSystemClipboard = async () =>
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || e.Source is not Visual source || ViewModel is not { } vm) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ActionItemViewModel action })
        {
            vm.SelectedAction = action;
            return;
        }
        for (Visual? v = source; v is not null && v != ListEditor; v = v.GetVisualParent())
        {
            if (v is Button { DataContext: EditorItemViewModel item })
            {
                vm.SelectCommand.Execute(item);
                return;
            }
        }
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EditorItemViewModel item }) ViewModel?.SelectCommand.Execute(item);
    }

    /// <summary>True when the key goes to a text field (its own copy/paste/undo apply).</summary>
    private static bool IsTextInput(object? source) =>
        source is Visual v && (v.FindAncestorOfType<TextBox>(includeSelf: true) is not null
                               || v.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not null
                               || v.FindAncestorOfType<CodeEditor>(includeSelf: true) is not null);

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ViewModel is not { } vm || vm.Picker.IsOpen || vm.Test.IsOpen || IsTextInput(e.Source)) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var inActionList = ActionList.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.C when command:
                e.Handled = true;
                await vm.CopySelectionAsync(wholeAction: inActionList);
                break;
            case Key.V when command:
                e.Handled = true;
                await vm.PasteCommand.ExecuteAsync(null);
                break;
            case Key.D when command:
                e.Handled = true;
                if (inActionList) vm.DuplicateActionCommand.Execute(null);
                else vm.DuplicateItemCommand.Execute(null);
                break;
            case Key.Z when command && shift:
            case Key.Y when command:
                e.Handled = true;
                if (vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null);
                break;
            case Key.Z when command:
                e.Handled = true;
                if (vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null);
                break;
            case Key.Delete when !inActionList && ListEditor.IsKeyboardFocusWithin:
                e.Handled = true;
                vm.DeleteItemCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// Shortcuts the lists would otherwise handle first (tunnel): Ctrl+Up/Down move the selected sub-action, Ctrl+O
    /// turns the selection on/off, Delete in the action list deletes the action (asks for a second press).
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.Picker.IsOpen || vm.Test.IsOpen || IsTextInput(e.Source)) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var inActionList = ActionList.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Up or Key.Down when command && !inActionList && vm.SelectedAction?.Selected is SubActionItemViewModel:
                e.Handled = true;
                (e.Key == Key.Up ? vm.MoveUpCommand : vm.MoveDownCommand).Execute(null);
                break;
            case Key.Home or Key.End when command && !inActionList && vm.SelectedAction?.Selected is SubActionItemViewModel:
                e.Handled = true;
                (e.Key == Key.Home ? vm.MoveToTopCommand : vm.MoveToBottomCommand).Execute(null);
                break;
            case Key.R when command && !inActionList && vm.SelectedAction?.Selected is SubActionItemViewModel { IsGroup: true }:
                e.Handled = true;
                RenameSelectedGroup();
                break;
            case Key.O when command:
                e.Handled = true;
                vm.ToggleEnabled(wholeAction: inActionList);
                break;
            case Key.Delete when inActionList && e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                vm.DeleteActionWithConfirm();
                break;
        }
    }

    /// <summary>Ctrl+R: puts the cursor in the selected group's name field (Streamer.bot's "Rename Group").</summary>
    private void RenameSelectedGroup()
    {
        var field = this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.DataContext is TextParameterViewModel { Descriptor.Key: "name" } && t.IsEffectivelyVisible);
        if (field is null) return;
        field.Focus();
        field.SelectAll();
    }

    private void OnPickerDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.Picker.ConfirmCommand.Execute(null);

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.Picker is not { } picker) return;
        switch (e.Key)
        {
            case Key.Enter:
                picker.ConfirmCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                picker.CancelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when picker.Options.Count > 0:
                var index = picker.Selected is null ? -1 : picker.Options.IndexOf(picker.Selected);
                index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, picker.Options.Count - 1);
                picker.Selected = picker.Options[index];
                e.Handled = true;
                break;
        }
    }
}
