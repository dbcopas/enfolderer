using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Enfolderer.App.Tokens;

public partial class TokensWindow : Window
{
    private readonly TokensViewModel _vm;

    public TokensWindow(string csvPath)
    {
        InitializeComponent();
        _vm = new TokensViewModel();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync(csvPath);
    }

    private void TokenSlot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is TokenSlot slot)
        {
            _vm.ToggleOwnership(slot);
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            if (e.Delta > 0 && _vm.PrevBinderCommand.CanExecute(null))
                _vm.PrevBinderCommand.Execute(null);
            else if (e.Delta < 0 && _vm.NextBinderCommand.CanExecute(null))
                _vm.NextBinderCommand.Execute(null);
        }
        else
        {
            if (e.Delta > 0 && _vm.PrevCommand.CanExecute(null))
                _vm.PrevCommand.Execute(null);
            else if (e.Delta < 0 && _vm.NextCommand.CanExecute(null))
                _vm.NextCommand.Execute(null);
        }
        e.Handled = true;
        base.OnPreviewMouseWheel(e);
    }
}
