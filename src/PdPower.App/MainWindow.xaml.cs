using System.Windows;
using System.Windows.Input;
using PdPower.App.ViewModels;

namespace PdPower.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>
    /// 스테퍼 휠 조작: 기본 ±1, Ctrl 동시 누름 ±0.1.
    /// 전압/전류 어느 쪽인지는 마우스가 올라간 요소의 Tag로 구분한다.
    /// </summary>
    private void OnStepperMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string axis }) return;

        double step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.1 : 1.0;
        double delta = e.Delta > 0 ? step : -step;

        var command = axis == "current" ? _viewModel.NudgeAmpsCommand : _viewModel.NudgeVoltsCommand;
        if (command.CanExecute(delta)) command.Execute(delta);

        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
