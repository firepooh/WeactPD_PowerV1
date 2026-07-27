# WeactPD_PowerV1 — WeAct PD Power Mini V1 (Buck) PC 제어 프로그램

[WeAct Studio PD Power Mini V1 BUCK](https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck) 장치를 PC에서 제어/모니터링하는 **Windows 10 C# (WPF)** 데스크톱 애플리케이션 개발 프로젝트.

## 1. 프로젝트 개요

| 항목 | 내용 |
|---|---|
| 대상 장치 | WeAct Studio PD Power Mini V1 BUCK (출력 1–20 V / 0.05–3 A) |
| 통신 방식 | USB CDC 가상 시리얼 포트 (기본) / UART (DP→TX, DM→RX, 3.3 V, CRC8) |
| 개발 환경 | Windows 10/11, Visual Studio 2022, .NET 8 (WPF) |
| 시리얼 | `System.IO.Ports.SerialPort` |
| 차트 | LiveCharts2 또는 ScottPlot (듀얼 Y축: 전압/전류) |
| 디자인 원본 | [`GUI/design/PD Power Tool Redesign.dc.html`](GUI/design/PD%20Power%20Tool%20Redesign.dc.html), 구현 스펙: [`GUI/design/CSHARP-SPEC.md`](GUI/design/CSHARP-SPEC.md) |
| 프로토콜 원본 | [`docs/protocol/`](docs/protocol/) (제조사 xlsx 2종 + Python 예제) |

### 실장비 검증 (2026-07-27, COM9)

USB CDC 모드로 아래 응답을 실제 수신 확인:

```
WHO_AM_I  (0x81) → "WeAct Studio PD Power Mini V1 BUCK"
VERSION   (0xC2) → "V1.0.2.0_6a997d9a"
SERIAL    (0xC3) → "acde8409ccd9"
```

## 2. 통신 프로토콜 요약

### 2.1 공통 사항

- 명령 1바이트가 프레임 선두. **읽기 명령은 `0x80` 비트를 OR** 한다 (예: OUTPUT_DATA 쓰기 `0x04`, 읽기 `0x84`).
- **USB CDC**: 프레임 종단 바이트 `0x0A`. 보레이트 무관 (가상 COM).
- **UART**: 종단 대신 **CRC8** 1바이트 (다항식 `0x31`, 초기값 `0xFF`, MSB-first bit 단위 처리). 보레이트 9600–460800 (장치 설정).
- 멀티바이트 값은 **리틀 엔디언** (`l8` = 하위, `h8` = 상위).
- 장치 응답도 동일 구조: `[cmd(0x80|x)] [payload...] [0x0A 또는 crc8]`.
- 문자열 응답(WHO_AM_I/VERSION/SERIAL)은 USB CDC에서 `[cmd][ascii...][0x0A]`, UART에서는 `[cmd][length][ascii...][crc8]`.

### 2.2 쓰기 명령 (PC → 장치)

| 명령 | Head | 페이로드 | 비고 |
|---|---|---|---|
| OUTPUT_EN | `0x02` | `x` | 0=enable, 1=disable ※주의: 반전 논리(xlsx 기준). 예제 py는 1=enable로 사용 — 실장비로 확인 필요 |
| OUTPUT_ID | `0x03` | `x` | 프리셋 그룹 M0–M4 (0–4) |
| OUTPUT_DATA | `0x04` | `id, v_l8, v_h8, i_l8, i_h8` | 전압 mV, 전류 mA |
| OUTPUT_OCP_EN | `0x06` | `x` | 과전류 보호 |
| OUTPUT_OFFSET_EN | `0x07` | `x` | 오프셋 보정 |
| BRIGHTNESS | `0x08` | `x` | 1–100 |
| OUTPUT_DISCHARGE_EN | `0x09` | `x` | 방전 기능 |
| INPUT_PD_VOLTAGE | `0x0A` | `v_l8, v_h8` | 단위 0.1 V, 8 V 이상. 출력 OFF & 출력전압 < 5 V 조건에서만 적용 |
| SYSTEM_RESET | `0x40` | — | |
| SYSTEM_CONFIG_SAVE | `0x44` | — | 휘발성 설정(ID/DATA/OCP/OFFSET/밝기/PD전압)을 플래시 저장 |
| SYSTEM_FACTORY_RESET | `0x45` | — | |

> 쓰기 명령 페이로드 값들은 **휘발성(Volatile)** — 전원 재인가 시 소실. 유지하려면 `SYSTEM_CONFIG_SAVE`(0x44) 필요 (GUI의 "Save" 버튼).

### 2.3 읽기 명령 (PC → 장치 → 응답)

| 명령 | Head | 응답 페이로드 | 비고 |
|---|---|---|---|
| WHO_AM_I | `0x81` | `info(ascii)` | 장치명 |
| READ_OUTPUT_STATE | `0x82` | `x` | bit0: output en, bit2-1: 01=CC, 10=OC, 00=정상(CV) |
| READ_OUTPUT_ID | `0x83` | `x` | 현재 프리셋 ID |
| READ_OUTPUT_DATA | `0x84` | `id, v_l8, v_h8, i_l8, i_h8` | 요청 시 `id` 1바이트 포함하여 전송 |
| READ_OUTPUT_DISPLAY | `0x85` | `v_l8, v_h8, i_l8, i_h8` | **실측** 전압(mV)/전류(mA) — 모니터링 폴링용 |
| READ_OUTPUT_OCP_EN | `0x86` | `x` | |
| READ_OUTPUT_OFFSET_EN | `0x87` | `x` | |
| READ_BRIGHTNESS | `0x88` | `x` | |
| READ_INPUT_STATE | `0x8A` | `state, v_l8, v_h8, pv_l8, pv_h8` | state 코드 아래 표, v=입력전압(mV), pv=PD요청전압(0.1 V) |
| READ_SYSTEM_VERSION | `0xC2` | `version(ascii)` | |
| READ_SYSTEM_SERIAL_NUM | `0xC3` | `serial(ascii)` | |

**INPUT_STATE state 코드**: 0=WAIT, 1=WAIT_PD_OK, 2=WAIT_QC_OK, 3=ERR, 4=QC, 5=PD, 6=DC

### 2.4 CRC8 (UART 모드 전용) — C# 구현

```csharp
public static byte Crc8(ReadOnlySpan<byte> data)
{
    byte crc = 0xFF;                    // 초기값
    foreach (byte b in data)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x31 : crc << 1);
    }
    return crc;
}
```

## 3. GUI 설계

목표 GUI는 제조사 예제 툴(PD Power Communication Tool v0.1.3)이 아니라 **자체 디자인안**을 따른다.
전체 스펙은 [`GUI/design/CSHARP-SPEC.md`](GUI/design/CSHARP-SPEC.md) 참조. 핵심:

- 창 1000×610, 좌측 레일(접이식 196↔56 px) + 우측 메인 2단 레이아웃
- **좌측 레일**: Monitor/Log 내비, 프리셋 M0–M4 카드(1클릭 적용·더블클릭 편집·출력 중 잠금), PD INPUT 카드(5/9/12/15/20 V), PORT 카드(COM 선택·연결)
- **메인**: 측정 스트립 3열(Voltage/Current/Power + Set 스테퍼·Output ON/OFF·CV/CC 배지), 듀얼축 Trend 차트, 푸터(SN·FW·입력 정보)
- 스테퍼: 휠 ±1 / Ctrl+휠 ±0.1, 범위 1–20 V / 0–3 A, 변경 즉시 장치 반영
- 샘플링 250 ms 폴링 (`READ_OUTPUT_DISPLAY` + `READ_OUTPUT_STATE`), 히스토리 64 포인트

## 4. 폴더 구조

```
WeactPD_PowerV1/
├─ README.md                  ← 본 문서
├─ docs/
│  └─ protocol/               ← 제조사 프로토콜 원본 (UART/USB xlsx, Python 예제)
├─ GUI/
│  └─ design/                 ← 목표 GUI 디자인안 (HTML 목업, C# 구현 스펙, 스크린샷)
└─ src/                       ← (예정) C# WPF 솔루션
   ├─ PdPower.Core/           ←   프로토콜 라이브러리 (프레임 인코딩/디코딩, CRC8, SerialPort)
   └─ PdPower.App/            ←   WPF GUI (MVVM)
```

## 5. 개발 로드맵

- [ ] **PdPower.Core**: 프로토콜 라이브러리
  - [ ] 프레임 빌더/파서 (USB CDC `0x0A` 종단 + UART CRC8 모드)
  - [ ] 명령 API (enable, preset, data set/get, display, input state, save 등)
  - [ ] 수신 스레드 + 타임아웃/재시도, 연결 해제 감지
- [ ] **콘솔 테스트 툴**: COM9 실장비 대상 전 명령 검증
- [ ] **PdPower.App (WPF, MVVM)**
  - [ ] 레이아웃 (레일 + 메인, GridSplitter)
  - [ ] 스테퍼 UserControl, 프리셋 카드, PORT 카드
  - [ ] 250 ms 폴링 + Trend 차트 (LiveCharts2)
  - [ ] Log 화면
- [ ] 설치본 패키징

## 6. 참고 링크

- 제조사 저장소: <https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck>
- 프로토콜 Python 예제: [`docs/protocol/com_pdpower.py`](docs/protocol/com_pdpower.py)

## 주의 사항

- `INPUT_PD_VOLTAGE` 변경은 **출력 OFF + 출력전압 5 V 미만**일 때만 적용됨
- 3 A 연속 출력은 방열 보강 필요 (2 A까지는 상시 가능)
- OUTPUT_EN 의 enable/disable 극성이 문서(xlsx)와 예제 코드(py) 간 상충 — 구현 시 실장비로 확정할 것
- UART 직결 시 3.3 V 레벨, 외부 UART 칩은 역전류 보호 필요
