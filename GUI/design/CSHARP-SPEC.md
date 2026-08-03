# PD Power Tool — 구현 스펙 (v0.2.6 최종 구성 기준)

대상 하드웨어: WeAct PD Power Mini V1.1 Buck · PC측 C#(WPF) 앱
디자인 참고: `PD Power Tool Redesign.dc.html`

## 창
- 1480 × 1010 px 기준(리사이즈 가능), 배경 #FFFFFF, 좌측 레일 296 px 고정폭
- 타이틀바: 앱 아이콘 + "PD Power Tool" + 최소화/최대화/닫기

## 색상 / 타이포
| 용도 | 값 |
|---|---|
| 카드·배경 | #FFFFFF |
| 보조 면(설정 셀, 비활성) | #FAFAF9 / #F2F2F0 |
| 구분선 | #EDEDEA / #F2F2F0 |
| 테두리 | #E4E4E1 / #DCDCD8 |
| 본문 / 보조 / 흐림 | #1A1A18 / #77776F / #A8A8A0 |
| 강조(Connect·전압 그래프·선택) | #3B7DF0 |
| 전류 그래프 | #E8703A |
| 연결 정상 | #3FB56A |

- UI 텍스트: 시스템 산세리프 13–15 px
- 수치: 모노스페이스. 측정값 44 px + 단위 접미 16 px, 설정값 17 px, 라벨 12–13 px

## 좌측 레일 (위 → 아래)
1. 로고 + "PD Power" + 버전(v0.2.6)
2. 내비: **Monitor**(선택) / Setup / Log — 선택 항목만 테두리 카드
3. **Presets** 카드 — M0–M4. 각 행 `[M칩] … — V  — A`
   - 미연결 시 값은 `—`, 전체 비활성(회색)
   - 1 클릭: Set voltage / Current limit에 즉시 반영
   - 더블 클릭: 행 내 편집(휠 ±1 / Ctrl+휠 ±0.1)
   - 출력 ON 중 잠금
4. **PD INPUT** 카드 — 우측에 실제 협상 전압, 하단 `− 20 V +` 스테퍼 + Set(변경 없으면 비활성)
5. **PORT** 카드 — 상태 라벨(OFFLINE/connected), COM 포트 드롭다운, 하단 Connect / Disconnect(강조 버튼)

## 메인
### 헤더
`Monitor` + 장치명(미연결 시 `—`), 우측 상태 칩 `OFFLINE / IDLE / RUN`

### 측정 + 제어 스트립 (한 카드, 3열 × 2행)
| 열 | 1행 (측정) | 2행 (제어) |
|---|---|---|
| 1 | `V` 라벨 + `0.000 V` (58 px) | Set voltage `− 5.000 V +` |
| 2 | `A` 라벨 + `0.000 A` (58 px) | Current limit `− 1.000 A +` |
| 3 | `W` `0.00 W` / `Wh` `0.000 Wh` 상하 2단 (34 px, Wh 옆 `RST` 초기화 버튼) | Output `ON | OFF` 세그먼트 + 우상단 CV/CC |

측정 라벨은 단어(Voltage/Current/Power) 대신 **V / A / W / Wh 약어**를 좌상단에 두고 숫자를 최대로 키움. 숫자 서체는 Chivo Mono(대체: Consolas), tabular-nums 고정폭.
Wh는 폴링 주기마다 `V × A × (Δt/3600)` 누적, 표시 자릿수는 10 미만 3자리 / 100 미만 2자리 / 이상 1자리로 자동 축소. `RST`는 누적값만 0으로 초기화(히스토리는 유지).

- 스테퍼: 휠 ±1, Ctrl+휠 ±0.1, −/+ 동일. 범위 1–20 V, 0–3 A. 변경 즉시 장치 반영
- Output은 연결 상태에서만 ON 가능

### Trend
- 툴바: 범례(Voltage/Current) · 범위 `1m | 5m | 1h` · 스케일 `Auto | Fit` · `CSV` · `Clear` · `Hide`
- 통계 줄: `V min/avg/max`, `A min/avg/max`, `W min/avg/max`, 샘플 수
- 이중 축: 좌 전압(상한 20 V) / 우 전류(상한 3 A), Auto는 피크 기준 1/2/2.5/5 단위 리스케일
- 데이터 없을 때 중앙에 `waiting for samples`
- X축 양 끝에 시작/종료 시각

### 푸터
좌: `SN — · FW — · —` / 우: 상태 안내문(예: `포트를 선택하고 연결하세요.`)

## 동작
- 폴링 250 ms, 히스토리 링버퍼(1m/5m/1h 범위별 다운샘플)
- 미연결: 모든 측정값 0.000, 프리셋·PD INPUT 비활성, 푸터 안내문 표시
- 부하 모델(시뮬레이터): I = V_set / R, I > 한계면 CC 진입 → V = I·R
- 출력 전압은 PD 협상 전압을 초과할 수 없음(벅)
- CSV: timestamp, V, A, W 열로 현재 버퍼 저장

## WPF 매핑
- 레일 = 고정폭 `Grid` 컬럼(필요 시 `GridSplitter`)
- 스테퍼 = `UserControl`, `PreviewMouseWheel`에서 `Keyboard.Modifiers`로 step 결정
- 차트 = LiveCharts2 / ScottPlot, Y축 2개(Left = V, Right = A)
- 시리얼 = `System.IO.Ports.SerialPort` + 250 ms `DispatcherTimer`
- 상태는 단일 `DeviceViewModel`(connected, output, setV, setI, pdRequest, pdActual, presets[5], hist)
