namespace PdPower.App;

/// <summary>Trend 차트 Y축 범위를 정하는 방식.</summary>
public enum YScaleMode
{
    /// <summary>0부터 시작해 피크에 맞춰 1/2/2.5/5 단위로 올려 잡는다 (기본).</summary>
    Auto,

    /// <summary>데이터 최소~최대에 맞춘다. 12.00 V 부근 리플처럼 좁은 대역을 볼 때.</summary>
    Fit,

    /// <summary>사용자가 휠로 직접 잡은 범위. 축 위에서 휠을 돌리면 이 모드로 넘어간다.</summary>
    Manual,
}
