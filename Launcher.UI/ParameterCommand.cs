using System.Windows.Input;

namespace Launcher.UI;

internal sealed class ParameterCommand<T>(Action<T> execute, Func<T, bool> enabled) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value && enabled(value);

    public void Execute(object? parameter)
    {
        if (parameter is T value && enabled(value)) execute(value);
    }
}
