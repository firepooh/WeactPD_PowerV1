namespace PdPower.Core;

/// <summary>포트 목록에서 무엇을 기본 선택할지 — 현재 선택 &gt; 마지막 연결 성공 &gt; 첫 항목.</summary>
public static class PortPreference
{
    public static string? Choose(IReadOnlyList<string> available, string? current, string? lastConnected)
        => available.FirstOrDefault(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase))
        ?? available.FirstOrDefault(n => string.Equals(n, lastConnected, StringComparison.OrdinalIgnoreCase))
        ?? available.FirstOrDefault();
}
