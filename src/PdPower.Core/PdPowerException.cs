namespace PdPower.Core;

/// <summary>장치 통신 관련 기본 예외.</summary>
public class PdPowerException : Exception
{
    public PdPowerException(string message) : base(message) { }
    public PdPowerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>프레임 구조·헤더·CRC 이상.</summary>
public sealed class PdPowerProtocolException : PdPowerException
{
    public PdPowerProtocolException(string message) : base(message) { }
}

/// <summary>응답 대기 시간 초과.</summary>
public sealed class PdPowerTimeoutException : PdPowerException
{
    public PdPowerTimeoutException(string message) : base(message) { }
}
