using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PdPower.App.ViewModels;

namespace PdPower.App;

/// <summary>
/// 미니 모드 — 440px 항상-위 위젯 (목업 2b). DataContext 는 본창의 MainViewModel 을 그대로 쓴다.
/// </summary>
public partial class MiniWindow : Window
{
    /// <summary>⤢ 버튼 — 본창 복귀 요청. 창 닫기는 MainWindow 가 처리한다.</summary>
    public event EventHandler? ExpandRequested;

    public MiniWindow()
    {
        InitializeComponent();
    }

    /// <summary>WindowStyle=None 은 각진 사각형이 된다 — 목업의 둥근 모서리를 DWM 에 요청한다(Win11).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2;   // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnExpand(object sender, RoutedEventArgs e)
        => ExpandRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>본창과 같은 규칙: 휠 ±1, Ctrl+휠 ±0.1. Tag 로 전압/전류 구분.</summary>
    private void OnStepperMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string axis } || DataContext is not MainViewModel vm) return;

        double step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.1 : 1.0;
        double delta = e.Delta > 0 ? step : -step;

        var command = axis == "current" ? vm.NudgeAmpsCommand : vm.NudgeVoltsCommand;
        if (command.CanExecute(delta)) command.Execute(delta);

        e.Handled = true;
    }

    /// <summary>
    /// 팝업을 버튼 바로 아래에 절대좌표로 연다 (목업의 top:100% 배치).
    /// PlacementTarget 상대 배치는 혼합 DPI 멀티모니터에서 엉뚱한 모니터에 뜨는
    /// WPF 버그가 있어 쓰지 않는다 — PointToScreen 은 앱의 가상화된 좌표계와 일치한다.
    /// </summary>
    private void OnPresetDropClick(object sender, RoutedEventArgs e)
    {
        if (PresetPopup.IsOpen)
        {
            PresetPopup.IsOpen = false;
            return;
        }

        var origin = PresetDrop.PointToScreen(new Point(0, PresetDrop.ActualHeight));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        PresetPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        PresetPopup.HorizontalOffset = origin.X / dpi.DpiScaleX;
        PresetPopup.VerticalOffset = origin.Y / dpi.DpiScaleY + 4;
        PresetPopup.IsOpen = true;
    }

    /// <summary>프리셋을 고르면 팝업을 닫는다 — Command 실행과 별개로 UI 만 정리.</summary>
    private void OnPresetChosen(object sender, RoutedEventArgs e)
        => PresetPopup.IsOpen = false;
}
