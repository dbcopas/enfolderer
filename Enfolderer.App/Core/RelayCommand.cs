using System;
using System.Windows.Input;

namespace Enfolderer.App;

/// <summary>
/// Basic ICommand implementation used for binder navigation and actions.
/// </summary>
public class RelayCommand : ICommand
{
	private readonly Action<object?> _execute;
	private readonly Predicate<object?>? _canExecute;
	public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
	{ _execute = execute; _canExecute = canExecute; }
	public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
	public void Execute(object? parameter) => _execute(parameter);
	public event EventHandler? CanExecuteChanged
	{
		add => CommandManager.RequerySuggested += value;
		remove => CommandManager.RequerySuggested -= value;
	}
}

