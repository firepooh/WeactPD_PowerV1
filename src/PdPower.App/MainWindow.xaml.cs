using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PdPower.App.ViewModels;

namespace PdPower.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // ViewModel 이 파일 대화상자를 직접 열지 않도록 여기서 붙여준다
        _viewModel.RequestSavePath = suggestedName =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                DefaultExt = ".csv",
                Filter = "CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*",
                AddExtension = true,
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
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

    /// <summary>드롭다운을 열 때마다 포트 목록을 다시 읽는다 — USB 장치가 중간에 꽂힐 수 있다.</summary>
    private void OnPortDropDownOpened(object sender, EventArgs e) => _viewModel.RefreshPorts();

    /// <summary>About 링크 — 기본 브라우저로 연다.</summary>
    private void OnNavigateUri(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ── 미니 모드 ────────────────────────────────────────────────────────

    private MiniWindow? _mini;

    /// <summary>
    /// 본창을 숨기고 항상-위 미니 위젯으로 전환한다. 같은 ViewModel 을 쓰므로
    /// 폴링·MCP 서버는 그대로 돌고, ⤢ 로 돌아오면 본창 상태도 이어진다.
    /// </summary>
    private void OnMiniModeClick(object sender, RoutedEventArgs e)
    {
        if (_mini is not null) return;

        _mini = new MiniWindow
        {
            DataContext = _viewModel,
            // 본창이 있던 화면의 같은 자리 우상단에 — 다른 모니터로 튀지 않는다
            Left = Left + Width - 440,
            Top = Top,
        };
        _mini.ExpandRequested += (_, _) => _mini?.Close();
        _mini.Closed += (_, _) =>
        {
            _mini = null;
            if (!IsVisible)
            {
                Show();
                Activate();
            }
        };

        _mini.Show();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
