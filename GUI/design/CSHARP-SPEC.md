# PD Power Tool — C# 구현 스펙 (WPF/WinForms)

디자인 원본: `PD Power Tool Redesign.dc.html` (브라우저에서 바로 열림)

## 창 구성
- 기본 창 1000 × 610 px, 흰색(#FFFFFF), 테두리 #E4E4E1, 모서리 12 px
- 좌측 레일 + 우측 메인 2단. 레일 기본 196 px, 드래그로 110–320 px 조절, 110 px 미만이면 56 px 아이콘 모드로 접힘(‹ 버튼으로도 토글)

## 색상
| 용도 | 값 |
|---|---|
| 배경/카드 | #FFFFFF |
| 보조 면(레일·설정 셀) | #FAFAF9 |
| 구분선 | #EDEDEA / #F2F2F0 |
| 테두리 | #E4E4E1 / #DCDCD8 |
| 본문 | #1A1A18 / 보조 #77776F / 흐림 #A8A8A0 |
| 강조(선택·ON·전압 그래프) | oklch(0.62 0.15 255) ≈ #3B7DF0 |
| 전류 그래프 | oklch(0.68 0.16 30) ≈ #E8703A |
| 연결 정상 | oklch(0.68 0.14 145) ≈ #3FB56A |

폰트: UI 산세리프 13 px 기준, 수치는 모노스페이스(JetBrains Mono / Consolas). 측정값 32 px, 설정값 17 px.

## 좌측 레일
1. 로고 + 앱명
2. 내비: Monitor(선택) / Log
3. Presets 카드 — M0–M4, 한 줄에 [칩][값]
   - 1 클릭: 해당 프리셋을 Set voltage / Current limit에 즉시 반영
   - 더블 클릭: 편집 모드(행 안에서 V·A 스테퍼, 휠 ±1 / Ctrl+휠 ±0.1)
   - 출력 ON 중에는 전체 잠금(LOCKED 표시, 클릭 무시, opacity 0.45)
4. PD INPUT 카드 — 요청 전압 5/9/12/15/20 V 단계 선택 + Set, 우측에 실제 협상 전압
5. PORT 카드 — COM 포트 드롭다운(플로팅, 창 크기 불변), 보드레이트·상태·Connect/Disconnect

## 메인
- 헤더: "Monitor" + 모델명, 우측 상태 칩(RUN / IDLE / OFFLINE)
- 중간 스트립(한 카드, 3열): 각 열 1행 = 측정값, 2행 = 제어
  - Voltage(V) / Set voltage 스테퍼
  - Current(A) / Current limit 스테퍼
  - Power(W) / Output ON·OFF 세그먼트 + CV·CC 배지
  - 스테퍼: 휠 ±1, Ctrl+휠 ±0.1, −/+ 버튼 동일. 범위 1–20 V, 0–3 A. 변경 즉시 적용
- Trend: 단일 차트 · 좌축 전압(상한 20 V) · 우축 전류(상한 3 A), 피크 기준 1/2/2.5/5 단위 오토스케일, Clear / Hide
- 푸터: SN · FW / Input Type PD · Input Voltage

## 동작 모델(시뮬레이션 기준)
- 샘플링 250 ms, 히스토리 64 포인트
- 저항 부하 R(기본 6 Ω): I = V_set / R, I > 전류 한계면 CC 진입 → V = I·R
- 출력 전압은 PD 입력 전압을 넘을 수 없음(벅)
- Disconnect 시 측정값 0, 히스토리 초기화

## 권장 매핑 (WPF)
- 레일 = `GridSplitter` 있는 `Grid` 컬럼, 접힘은 컬럼 Width 56/196 전환
- 스테퍼 = `UserControl`(PreviewMouseWheel에서 Ctrl 여부로 step 결정)
- 차트 = LiveCharts2 또는 ScottPlot, Y축 2개(Left/Right)
- 시리얼 = `System.IO.Ports.SerialPort`, 250 ms `DispatcherTimer` 폴링
