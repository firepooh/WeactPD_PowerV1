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

### 실장비 검증 (2026-07-27, COM9 / USB CDC)

CLI(`selftest`)와 WPF 앱 양쪽에서 아래를 실제로 확인:

```
장치명    : WeAct Studio PD Power Mini V1 BUCK
펌웨어    : V1.0.2.0_6a997d9a
시리얼    : acde8409ccd9
입력      : PD 20.111 V (PD 요청 20.0 V)
활성설정  : M4 = 5.000 V / 1.000 A
프리셋    : M0 1.0V/0.2A · M1 3.3V/0.5A · M2 5.0V/1.0A · M3 9.0V/2.0A · M4 5.0V/1.0A
```

읽기 계열 명령(WHO_AM_I, VERSION, SERIAL_NUM, OUTPUT_STATE, OUTPUT_ID, OUTPUT_DATA,
OUTPUT_DISPLAY, OCP_EN, OFFSET_EN, BRIGHTNESS, INPUT_STATE)은 전부 실장비 응답 확인 완료.
쓰기 계열은 출력 단자에 전압이 인가되므로 미검증 — `PdPower.Cli` 로 직접 확인할 수 있다.

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
| OUTPUT_EN | `0x02` | `x` | ⚠ xlsx는 `0=enable`, 제조사 py 예제는 `1=enable` — 상충. Core 기본값은 `1=enable`, 확정은 `PdPower.Cli probe-outputen` 참조 |
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

![PdPower.App Monitor 화면 — COM9 실장비 연결](docs/images/app-monitor.png)

목표 GUI는 제조사 예제 툴(PD Power Communication Tool v0.1.3)이 아니라 **자체 디자인안**을 따른다.
치수·색상의 최종 근거는 목업 HTML [`GUI/design/PD Power Tool Redesign.dc.html`](GUI/design/PD%20Power%20Tool%20Redesign.dc.html)
이고, 요약 스펙은 [`GUI/design/CSHARP-SPEC.md`](GUI/design/CSHARP-SPEC.md) 다.
**요약 스펙만 보고 구현하면 팔레트는 맞아도 구조가 틀어진다** — 반드시 목업을 렌더링해서 확인할 것. 핵심:

- 창 1000×610, 좌측 레일(접이식 196↔56 px) + 우측 메인 2단 레이아웃
- **좌측 레일**: Monitor/Log 내비, 프리셋 M0–M4 카드(1클릭 적용·더블클릭 편집·출력 중 잠금), PD INPUT 카드(5/9/12/15/20 V), PORT 카드(COM 선택·연결)
- **메인**: 측정 스트립 3열(Voltage/Current/Power + Set 스테퍼·Output ON/OFF·CV/CC 배지), 듀얼축 Trend 차트, 푸터(SN·FW·입력 정보)
- 스테퍼: 휠 ±1 / Ctrl+휠 ±0.1, 범위 1–20 V / 0–3 A, 변경 즉시 장치 반영
- 샘플링 250 ms 폴링 (`READ_OUTPUT_DISPLAY` + `READ_OUTPUT_STATE`), 히스토리 64 포인트

### 디자인 재현에서 놓치기 쉬운 것들

목업의 "느낌"은 대부분 아래 구조에서 나온다. WPF 기본 컨트롤을 그대로 두면 Aero 시절 크롬이
플랫 카드와 섞여 전체가 촌스러워지므로, `Themes/Theme.xaml` 에서 ComboBox·CheckBox·ListBox까지
전부 템플릿을 교체했다.

| 항목 | 올바른 구조 |
|---|---|
| 창 | 외곽 여백 0. 레일은 `#FAFAF9` 면 + 오른쪽 헤어라인, 헤더/푸터도 헤어라인으로 구분 |
| 측정 셀 | 흰 상단(값) + `#FAFAF9` 하단(제어)을 `#F2F2F0` 헤어라인으로 나눈 2단 |
| 값 표기 | 32 px 모노 숫자 + 13 px 회색 단위, **베이스라인 정렬** (캡션에 단위를 넣지 않는다) |
| 스테퍼 | `[− │ 값 │ +]` 를 테두리 하나(radius 7)로 묶고 내부만 헤어라인 |
| 출력 | 토글 버튼이 아니라 `ON│OFF` 세그먼트, 활성 칸만 액센트로 채움 |
| 배지 | `CV`/`CC`/`OC` 2글자 (열거형 이름 그대로 쓰면 길어서 깨진다) |
| Trend | 탭 컨트롤이 아니라 카드 안 섹션 — 제목 + 범례 + Clear/Hide 를 한 줄에 |
| 밀도 | 본문 11–13 px. 기본 WPF 크기를 쓰면 계기판이 아니라 웹 폼처럼 보인다 |

Trend 차트는 [`Controls/TrendChart.cs`](src/PdPower.App/Controls/TrendChart.cs) 에서 직접 그린다.
샘플이 64개로 고정이라 차트 라이브러리보다 가볍고, 축 오토스케일(1/2/2.5/5 단위)을 스펙대로 맞추기 쉽다.

![Log 화면 — 원시 프레임 트레이스](docs/images/app-log.png)

## 4. 솔루션 구조

```
WeactPD_PowerV1/
├─ WeactPD_PowerV1.sln
├─ README.md                      ← 본 문서
├─ docs/protocol/                 ← 제조사 프로토콜 원본 (UART/USB xlsx, Python 예제)
├─ GUI/design/                    ← 목표 GUI 디자인안 (HTML 목업, C# 구현 스펙, 스크린샷)
├─ src/
│  ├─ PdPower.Core/               ← 프로토콜 라이브러리 (net8.0)
│  │  ├─ Protocol/
│  │  │  ├─ PdCommand.cs          ←   명령 코드 enum
│  │  │  ├─ ProtocolMode.cs       ←   UsbCdc / Uart
│  │  │  ├─ Crc8.cs               ←   CRC-8 (0x31, init 0xFF)
│  │  │  └─ Frame.cs              ←   프레임 인코딩/디코딩, 응답 길이표
│  │  ├─ Models/                  ←   DeviceInfo, OutputStatus, InputStatus 등
│  │  ├─ PdPowerDevice.cs         ←   장치 API (SerialPort 요청/응답)
│  │  └─ PdPowerException.cs
│  ├─ PdPower.Cli/                ← 실장비 검증 콘솔 도구 (net8.0)
│  └─ PdPower.App/                ← WPF GUI (net8.0-windows, MVVM)
│     ├─ Themes/Theme.xaml        ←   팔레트 + 컨트롤 템플릿 전체 교체
│     ├─ Controls/TrendChart.cs   ←   듀얼축 시계열 차트 (직접 렌더링)
│     ├─ ViewModels/MainViewModel.cs
│     ├─ Converters.cs
│     └─ MainWindow.xaml
└─ tests/PdPower.Core.Tests/      ← xUnit — CRC8/프레임 검증 26개
```

### 빌드 · 실행

```bash
dotnet build WeactPD_PowerV1.sln
```

```bash
dotnet test tests/PdPower.Core.Tests/PdPower.Core.Tests.csproj
```

CLI로 실장비 확인 (읽기 전용):

```bash
dotnet run --project src/PdPower.Cli -- --port COM9 selftest
```

WPF 앱 실행:

```bash
dotnet run --project src/PdPower.App
```

### 구현 시 주의점

- `Frame.ResponseLength()` 로 응답 길이를 고정해 프레임을 잘라낸다. **종단 바이트(0x0A) 탐색만으로는
  프레임을 나눌 수 없다** — 예를 들어 2570 mV(`0x0A0A`)처럼 페이로드에 0x0A가 들어갈 수 있다.
  ASCII 응답(WHO_AM_I/VERSION/SERIAL)만 종단 탐색을 쓴다.
- 요청 직전에 수신 버퍼를 비워(`DiscardInBuffer`) 프레임 동기를 잡는다.
- `PdPowerDevice.FrameExchanged` 이벤트는 **스레드 풀에서 발생**한다. UI 컬렉션을 갱신하려면
  구독자가 디스패처로 마샬링해야 한다 (안 하면 `NotSupportedException`).
- `READ_INPUT_STATE`(0x8A)는 PD Power Mini V1 펌웨어 **v1.0.2.0 이상**에서만 지원 —
  실패를 정상 흐름으로 처리한다.

## 5. 개발 로드맵

- [x] **PdPower.Core**: 프로토콜 라이브러리
  - [x] 프레임 빌더/파서 (USB CDC `0x0A` 종단 + UART CRC8 모드)
  - [x] 명령 API (enable, preset, data set/get, display, input state, 보호 설정, save 등)
  - [x] 요청/응답 직렬화 + 타임아웃, 연결 해제 감지
  - [x] 단위 테스트 — 문서의 CRC8 정답값 15개로 검증
  - [ ] SYSTEM_FACTORY_DATA(0x47) 캘리브레이션 값 읽기/쓰기
- [x] **PdPower.Cli**: 실장비 검증 도구 (읽기 명령 전체 확인 완료)
- [x] **PdPower.App (WPF, MVVM)**
  - [x] 목업 기준 레이아웃 — 여백 0, 톤 있는 레일, 2단 측정 셀, 통합 스테퍼 알약
  - [x] 기본 컨트롤 템플릿 전체 교체 (ComboBox·CheckBox·ListBox·버튼)
  - [x] 레일 내 Monitor/Log 내비, 프리셋 M0–M4 (1클릭 적용, 출력 중 잠금)
  - [x] PD INPUT 스테퍼, PORT 카드 (드롭다운 열 때 포트 자동 갱신)
  - [x] 250 ms 폴링 → 실측 V/A/W, CV/CC/OC 배지, RUN/IDLE/OFFLINE 칩
  - [x] `ON│OFF` 세그먼트 출력 제어
  - [x] 스테퍼 (−/+ 버튼, 휠 ±1 / Ctrl+휠 ±0.1)
  - [x] 듀얼축 Trend 차트 + 오토스케일, Clear/Hide
  - [x] Log 화면 (원시 프레임 트레이스 토글)
  - [ ] 레일 56 px 아이콘 모드 접힘
  - [ ] 프리셋 더블클릭 인라인 편집
  - [ ] Trend 시간 범위 선택 (1m / 5m / 1h)
- [ ] 설치본 패키징

## 6. 참고 링크

- 제조사 저장소: <https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck>
- 프로토콜 Python 예제: [`docs/protocol/com_pdpower.py`](docs/protocol/com_pdpower.py)

## 주의 사항

- `INPUT_PD_VOLTAGE` 변경은 **출력 OFF + 출력전압 5 V 미만**일 때만 적용됨
- 3 A 연속 출력은 방열 보강 필요 (2 A까지는 상시 가능)
- **OUTPUT_EN 극성 미확정** — 문서(xlsx)와 예제 코드(py)가 상충한다. 출력 단자에 전압이 인가되는
  동작이라 자동 검증을 하지 않았다. 부하를 분리한 뒤 아래로 확정할 것:

  ```bash
  dotnet run --project src/PdPower.Cli -- --port COM9 probe-outputen
  ```

  판정 결과가 `0x00` 이면 `PdPowerDevice.OutputEnableOnValue` 기본값을 `0x00` 으로 바꾼다.
- 프리셋·PD 전압 등 쓰기 값은 휘발성 — `SYSTEM_CONFIG_SAVE`(GUI의 Save) 없이는 전원 재인가 시 소실
- UART 직결 시 3.3 V 레벨, 외부 UART 칩은 역전류 보호 필요
