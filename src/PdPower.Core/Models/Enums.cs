namespace PdPower.Core.Models;

/// <summary>READ_INPUT_STATE(0x8A)가 보고하는 입력 협상 상태.</summary>
public enum InputState : byte
{
    Wait = 0,
    WaitPdOk = 1,
    WaitQcOk = 2,
    Error = 3,
    Qc = 4,
    Pd = 5,
    Dc = 6,
}

/// <summary>READ_OUTPUT_STATE(0x82)의 bit2-1이 보고하는 레귤레이션 모드.</summary>
public enum OutputRegulation
{
    /// <summary>정전압 동작(정상).</summary>
    ConstantVoltage = 0,

    /// <summary>정전류 동작 — 전류 제한에 걸린 상태.</summary>
    ConstantCurrent = 1,

    /// <summary>과전류 보호 동작.</summary>
    OverCurrent = 2,
}
