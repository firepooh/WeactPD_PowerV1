using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

        RestoreFromSettings();
    }

    /// <summary>저장된 MCP·미니 모드 상태를 복원한다. 창 위치는 OnSourceInitialized 가 맡는다.</summary>
    private void RestoreFromSettings()
    {
        var s = _viewModel.Settings;

        if (s.McpEnabled && _viewModel.McpOnCommand.CanExecute(null))
            _viewModel.McpOnCommand.Execute(null);

        // 미니 상태로 종료됐던 경우(프로세스 강제 종료 등) 미니로 시작한다
        if (s.MiniMode)
            Loaded += (_, _) => OpenMini();
    }

    /// <summary>
    /// 창 위치 복원 — <b>물리 픽셀 + Win32</b> 로 다룬다. WPF DIP 좌표로 다른 배율의
    /// 모니터에 배치하면 PerMonitorV2 초기 배치가 어긋난다 (실측: 모니터3 저장분이
    /// 주 모니터 엉뚱한 위치로 복원됐다).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var s = _viewModel.Settings;
        if (s is { WindowLeft: { } l, WindowTop: { } t, WindowWidth: { } w, WindowHeight: { } h }
            && w > 400 && h > 300 && IsOnVirtualScreenPx(l, t, w, h))
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, (int)l, (int)t, (int)w, (int)h,
                         0x0014 /* SWP_NOZORDER | SWP_NOACTIVATE */);
        }
    }

    /// <summary>미니 창 좌표(DIP)용 화면 범위 검사.</summary>
    private static bool IsOnVirtualScreen(double left, double top, double width, double height)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        return left < vl + vw && left + width > vl && top < vt + vh && top + height > vt;
    }

    /// <summary>본창 좌표(물리 px)용 화면 범위 검사.</summary>
    private static bool IsOnVirtualScreenPx(double left, double top, double width, double height)
    {
        int vl = GetSystemMetrics(76), vt = GetSystemMetrics(77);   // SM_X/YVIRTUALSCREEN
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);   // SM_CX/CYVIRTUALSCREEN
        return left < vl + vw && left + width > vl && top < vt + vh && top + height > vt;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private struct Win32Rect { public int Left, Top, Right, Bottom; }

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
    private void OnMiniModeClick(object sender, RoutedEventArgs e) => OpenMini();

    private void OpenMini()
    {
        if (_mini is not null) return;

        var s = _viewModel.Settings;
        var mini = new MiniWindow { DataContext = _viewModel };

        // 마지막 미니 위치 > 본창 우상단 — 다른 모니터로 튀지 않는다
        if (s is { MiniLeft: { } ml, MiniTop: { } mt } && IsOnVirtualScreen(ml, mt, mini.Width, 150))
        {
            mini.Left = ml;
            mini.Top = mt;
        }
        else
        {
            mini.Left = Left + Width - mini.Width;
            mini.Top = Top;
        }

        mini.ExpandRequested += (_, _) => _mini?.Close();
        mini.Closed += (_, _) =>
        {
            s.MiniLeft = mini.Left;
            s.MiniTop = mini.Top;
            _mini = null;
            if (!IsVisible)
            {
                s.MiniMode = false;   // 본창으로 복귀한 상태로 저장
                Show();
                Activate();
            }
            s.Save();
        };

        _mini = mini;
        s.MiniMode = true;   // 이 상태에서 프로세스가 죽으면 다음 실행은 미니로
        s.Save();
        mini.Show();
        Hide();
    }

    /// <summary>
    /// 창 위치는 파괴 전에 잡아야 한다 — OnClosed(WmDestroy) 시점에는 창 좌표가
    /// 유효하지 않다 (RestoreBounds 는 무한대 값이 되어 JSON 저장이 터졌다 — 실측).
    /// 물리 픽셀로 저장한다 (OnSourceInitialized 의 복원과 짝).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal
            && GetWindowRect(new WindowInteropHelper(this).Handle, out var r)
            && r.Right > r.Left && r.Bottom > r.Top)
        {
            var s = _viewModel.Settings;
            s.WindowLeft = r.Left;
            s.WindowTop = r.Top;
            s.WindowWidth = r.Right - r.Left;
            s.WindowHeight = r.Bottom - r.Top;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();   // 설정 저장 포함
        base.OnClosed(e);

        // Kestrel(MCP) 이 수 분 동안 프로세스를 붙잡아 유령이 된다 — 유령은 COM 포트와
        // 5115 포트를 쥔 채 다음 실행을 깨뜨리므로, 정리가 끝난 지금 즉시 끝낸다.
        Environment.Exit(0);
    }
}
