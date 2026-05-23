using System;
using System.Windows.Input;

namespace BlockParam.UI.Controls.PillMultiSelect;

// Minimal ICommand for the pill control's footer buttons and toggle action.
// Kept here so the folder has zero dependency on a host MVVM helper.
//
// `public`, not `internal`: instances of this class are exposed through the
// public `ICommand` properties on PillMultiSelectInternalState and bound via
// `Command="{Binding ClearCommand}"` etc. Interface dispatch handles
// Execute/CanExecute, but CommandManager.RequerySuggested re-evaluation and
// some WPF binding diagnostics may reflect on the concrete type — keep the
// type publicly reachable under TIA's partial-trust SandboxDomain so the
// same #141 hazard class can't bite the command path.
public sealed class PillRelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public PillRelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public PillRelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
