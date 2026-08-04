using System.IO;
using System.Text.Json;

namespace PdPower.App;

/// <summary>
/// 재시작해도 유지할 앱 설정. %AppData%\PdPowerTool\settings.json 에 저장한다.
/// </summary>
/// <remarks>
/// 설정 파일이 없거나 깨져 있으면 조용히 기본값으로 시작한다 — 설정 때문에
/// 앱이 못 뜨는 일은 없어야 한다. 저장 실패도 같은 이유로 무시한다.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>마지막으로 연결에 성공한 COM 포트. 시작 시 포트 목록에서 우선 선택된다.</summary>
    public string? LastPort { get; set; }

    /// <summary>MCP 서버 자동 시작 — 마지막으로 켜둔 상태를 복원한다.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>MCP 읽기 전용 — 제어 도구를 거부한다.</summary>
    public bool McpReadOnly { get; set; }

    public int? PollIntervalMs { get; set; }
    public int? StatusDivisor { get; set; }
    public int? TrendRangeSeconds { get; set; }

    // 본창 위치/크기 (WPF DIP). null 이면 기본값.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>종료 당시 미니 모드였는지 — 다음 실행을 미니로 시작한다.</summary>
    public bool MiniMode { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PdPowerTool", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or DirectoryNotFoundException or FileNotFoundException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or JsonException)
        {
            // 설정 저장 실패는 치명적이지 않다 — 다음 실행에서 기본값으로 돌아갈 뿐.
            // ArgumentException: Infinity 같은 직렬화 불가 값이 끼어들어도 앱을 죽이지 않는다
            // (종료 경로에서 터지면 유령 프로세스가 된다 — 실측으로 확인한 사고).
        }
    }
}
