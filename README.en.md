# WeactPD_PowerV1 — PC control tool for WeAct PD Power Mini V1 (Buck)

[한국어](README.md) · **English** (this document)

A **Windows C# (WPF, .NET 8)** desktop application that controls and monitors the
[WeAct Studio PD Power Mini V1 BUCK](https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck)
programmable power supply.

## 1. Overview

| Item | Detail |
|---|---|
| Target device | WeAct PD Power Mini V1 BUCK (output 1–20 V / 0.05–3 A) |
| Transport | USB CDC virtual serial (default) / UART (3.3 V, CRC8) — `System.IO.Ports` |
| Design source | [`GUI/design/`](GUI/design/) (HTML mockup + C# spec) — an in-house design, not the vendor tool |
| Protocol source | [`docs/protocol/`](docs/protocol/) (vendor xlsx ×2 + Python example) |
| AI control | Built-in MCP server — toggle in Setup, lets Claude etc. read/control the device (§4) |

Every read/write command and all 10 MCP tools have been verified on real hardware
(COM9, firmware `V1.0.2.0_6a997d9a`); the measurements in §9–11 come from the same unit.

## 2. Protocol summary

- First byte is the command; **read commands OR in `0x80`** (write `0x04` → read `0x84`).
- **USB CDC**: terminator `0x0A`. **UART**: CRC8 instead (poly `0x31`, init `0xFF`, MSB-first).
- Multi-byte values are **little-endian**. Responses mirror the structure
  `[cmd|0x80][payload...][0x0A|crc8]`; string responses are `[cmd][ascii...][0x0A]` on CDC,
  `[cmd][len][ascii...][crc8]` on UART.

### 2.1 Write commands (PC → device)

| Command | Head | Payload | Notes |
|---|---|---|---|
| OUTPUT_EN | `0x02` | `x` | **`1 = enable`** (hardware-confirmed; the xlsx note `0=enable` is wrong, §8) |
| OUTPUT_ID | `0x03` | `x` | Preset M0–M4 (0–4) |
| OUTPUT_DATA | `0x04` | `id, v_l8, v_h8, i_l8, i_h8` | Voltage mV, current mA |
| OUTPUT_OCP_EN | `0x06` | `x` | Over-current protection |
| OUTPUT_OFFSET_EN | `0x07` | `x` | Offset correction |
| BRIGHTNESS | `0x08` | `x` | 1–100 |
| OUTPUT_DISCHARGE_EN | `0x09` | `x` | Discharge |
| INPUT_PD_VOLTAGE | `0x0A` | `v_l8, v_h8` | 0.1 V units, ≥8 V; applied only while output OFF & Vout<5 V |
| SYSTEM_RESET | `0x40` | — | |
| SYSTEM_CONFIG_SAVE | `0x44` | — | Persists the six items below at once |
| SYSTEM_FACTORY_RESET | `0x45` | — | |

All written values are **volatile** — lost on power cycle unless `SYSTEM_CONFIG_SAVE` (0x44,
the GUI Save button) is sent. It persists `OUTPUT_ID`, `OUTPUT_DATA`, `OCP_EN`, `OFFSET_EN`,
`BRIGHTNESS`, `INPUT_PD_VOLTAGE` (no individual selection). Exceptions:

- `OUTPUT_EN` — never persisted; output always starts OFF after power-up (safety).
- `OUTPUT_DISCHARGE_EN` — volatile *and* not in the save list: **cannot be made permanent**.
- `SYSTEM_LCD_PANEL_TYPE` (`0x46`) — the opposite: **non-volatile**, written immediately.

There is no flash read-back command (reads always return RAM values), so the GUI's `UNSAVED`
badge only tracks **changes this app made since connecting** — knob changes are unknowable.

### 2.2 Read commands (PC → device → response)

| Command | Head | Response payload | Notes |
|---|---|---|---|
| WHO_AM_I | `0x81` | `info(ascii)` | Device name |
| READ_OUTPUT_STATE | `0x82` | `x` | bit0: output en, bits2-1: 01=CC, 10=OC, 00=CV |
| READ_OUTPUT_ID | `0x83` | `x` | Current preset id |
| READ_OUTPUT_DATA | `0x84` | `id, v_l8, v_h8, i_l8, i_h8` | Request carries one `id` byte |
| READ_OUTPUT_DISPLAY | `0x85` | `v_l8, v_h8, i_l8, i_h8` | **Measured** V/I — used for polling |
| READ_OUTPUT_OCP_EN | `0x86` | `x` | |
| READ_OUTPUT_OFFSET_EN | `0x87` | `x` | |
| READ_BRIGHTNESS | `0x88` | `x` | |
| READ_INPUT_STATE | `0x8A` | `state, v_l8, v_h8, pv_l8, pv_h8` | v=input mV, pv=PD request (0.1 V); firmware v1.0.2.0+ |
| READ_SYSTEM_VERSION | `0xC2` | `version(ascii)` | |
| READ_SYSTEM_SERIAL_NUM | `0xC3` | `serial(ascii)` | |

**INPUT_STATE codes**: 0=WAIT, 1=WAIT_PD_OK, 2=WAIT_QC_OK, 3=ERR, 4=QC, 5=PD, 6=DC

### 2.3 CRC8 (UART only)

```csharp
public static byte Crc8(ReadOnlySpan<byte> data)
{
    byte crc = 0xFF;
    foreach (byte b in data)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x31 : crc << 1);
    }
    return crc;
}
```

## 3. GUI

![PdPower.App Monitor view — real device on COM9](docs/images/app-monitor.png)

- 1000×680 window, left rail + main area. Rail: Monitor/Setup/Log nav, presets M0–M4,
  PD INPUT and PORT cards. Main: three measurement cells + steppers, ON│OFF segment,
  CV/CC badge, Trend chart, footer.
- Measurement cells use **V / A / W / Wh abbreviations** with large digits (58px) instead of
  word labels. The third cell stacks power (W) and **accumulated energy (Wh)** — integrated as
  `V×A×Δt` per poll, reset via `RST` (Trend history is kept), also exposed as `energyWh`
  in MCP `get_status`.
- Steppers: wheel ±1 / Ctrl+wheel ±0.1, applied to the device immediately.
- The authoritative design source is the HTML mockup in [`GUI/design/`](GUI/design/) —
  **implementing from the summary spec alone gets the structure wrong; render the mockup.**
- All stock WPF control templates are replaced in `Themes/Theme.xaml`. Buttons whose colors
  change must take `Background`/`BorderBrush` via `TemplateBinding` (hard-coding silently
  defeats caller `DataTrigger`s), and hover states should modulate opacity, not colors.

### Trend

| Feature | Behavior |
|---|---|
| `1m` `5m` `1h` | Visible window — storage interval scales with it (§9) |
| `Auto` / `Fit` | Y-axis 0→peak / hug the data range (for ripple) |
| **Click on plot** | Freeze/resume — freezing pins a snapshot (`MeasurementWindow`); polling continues |
| **Wheel over an axis** | Manual Y zoom — left half = voltage, right half = current; `Auto`/`Fit` return |
| `CSV` | Export the visible window (`timestamp,volts,amps,watts,regulation,output_enabled`) |
| Cursor / state band / stats | Nearest-sample readout, CV/CC/OC + output timeline, window min/avg/max |

The chart ([`TrendChart.cs`](src/PdPower.App/Controls/TrendChart.cs)) renders directly: the
x-axis is time, and with more points than pixels it draws **per-column min/max** — even
resampling would erase exactly the spikes worth seeing. Frozen/manual states get in-plot badges.

## 4. AI control — built-in MCP server

Enable **AI control server** in Setup and an MCP server starts inside the app, letting AI
clients like Claude read and control the device. A COM port belongs to one process, so
**the server lives in the GUI process that owns the port.** Official SDK
([`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)),
Streamable HTTP at `http://localhost:5115`, localhost only, off by default.

The **Copy register cmd** button in Setup copies the registration line:

```bash
claude mcp add --transport http pdpower http://localhost:5115
```

| Tool | Action |
|---|---|
| `get_status` | connection, output, CV/CC/OC, measured V/A/W, PD input, active preset |
| `get_settings` | presets M0–M4, OCP, brightness, PD voltage — **re-read from the device (knob changes included)** |
| `get_history_stats` | min/avg/max of V/A/W over the visible Trend window |
| `set_output` / `set_setpoint` | output on/off; voltage/current (either argument optional) |
| `select_preset` / `set_pd_voltage` | **refused while output is on** (same rule as the GUI) |
| `set_ocp` / `set_brightness` / `save_config` | same as the Setup controls |

Control requests are marshalled to the UI thread and run through **the same code paths as the
GUI buttons** (screen and UNSAVED state stay in sync); every AI command is logged with an
`[MCP]` prefix. All 10 tools hardware-verified (2026-08-03).

SDK 2.0 notes: ① nullable parameters also need a **`= null` default** to be optional in the
schema; ② tool exceptions must be `McpException` or the SDK masks the message;
③ the `Microsoft.AspNetCore.App` framework reference flows to the App, so the
framework-dependent build **also needs the ASP.NET Core Runtime**.

## 5. Solution layout

```
WeactPD_PowerV1/
├─ Directory.Build.props          ← shared version (VersionPrefix)
├─ .githooks/pre-commit           ← bumps the patch version every commit
├─ .github/workflows/build.yml    ← build & test; Release on tags
├─ docs/protocol/                 ← vendor protocol sources
├─ GUI/design/ · GUI/icon/        ← design mockup · app icon sources
├─ src/
│  ├─ PdPower.Core/               ← protocol library (Frame, Crc8, PdPowerDevice,
│  │                                 MeasurementHistory — time-based ring buffer)
│  ├─ PdPower.Cli/                ← hardware verification console (selftest/bench/test-ocp …)
│  ├─ PdPower.Mcp/                ← 10 MCP tools + localhost:5115 Kestrel host
│  └─ PdPower.App/                ← WPF GUI (Theme.xaml templates, TrendChart,
│                                    MainViewModel + MainViewModel.Mcp.cs)
└─ tests/PdPower.Core.Tests/      ← xUnit ×42
```

### Versioning · releases

One version source: `VersionPrefix` in `Directory.Build.props`. Once after cloning:

```bash
git config core.hooksPath .githooks
```

Every commit then bumps the patch digit (`SKIP_VERSION_BUMP=1` to skip; the hook steps aside
during merges/rebases). CI decides the final version: `master` pushes produce
`<prefix>-dev.<run>` artifacts; **pushing a tag `v1.2.3` creates a GitHub Release**
(tests run before publish, so a red suite blocks the release):

```bash
git tag v0.2.0 && git push origin v0.2.0
```

| Release asset | Contents |
|---|---|
| `PdPowerTool.exe` | GUI single exe — needs .NET 8 Desktop + ASP.NET Core Runtime |
| `PdPowerTool-standalone.exe` | GUI, self-contained |
| `PdPowerCli.exe` / `PdPowerCli-standalone.exe` | CLI (the former needs the .NET 8 Runtime) |

### Build · run

```bash
dotnet build WeactPD_PowerV1.sln
dotnet test
dotnet run --project src/PdPower.Cli -- --port COM9 selftest   # hardware check (read-only)
dotnet run --project src/PdPower.App
```

### Implementation pitfalls

- Responses are sliced by fixed length (`Frame.ResponseLength()`). **Scanning for 0x0A cannot
  delimit frames** — payloads can contain it (2570 mV = `0x0A0A`). Only ASCII responses scan.
- `DiscardInBuffer` right before each request to regain frame sync.
- `FrameExchanged` fires on a thread-pool thread — marshal before touching UI collections.
- Sliders need debouncing (brightness coalesces writes over 250 ms, with a suppression flag
  when filling the slider from a device read).
- `READ_INPUT_STATE` needs firmware v1.0.2.0+ — treat failure as a normal path.

## 6. Roadmap

Done: protocol library (+42 tests), CLI, mockup-faithful GUI (background polling, Trend
tooling, Setup, auto-reconnect), MCP server, icons, versioning/release automation.

Open: offset correction & discharge in Setup, reboot/factory-reset (behind a calibration
backup), factory data (0x47) read/write, CV badge shown while disconnected, rail collapse mode,
inline preset editing, wider app-settings persistence (the last COM port is already saved to
`%AppData%\PdPowerTool\settings.json`), triggered burst capture, power (W) series,
installer packaging.

## 7. References

- Vendor repository: <https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck>
- Protocol Python example: [`docs/protocol/com_pdpower.py`](docs/protocol/com_pdpower.py)

## 8. Cautions

- `INPUT_PD_VOLTAGE` applies only while **output OFF and Vout < 5 V**.
- Continuous 3 A needs extra cooling (2 A is fine continuously).
- **`*_EN` commands are `1 = enable`** — all four `0=enable` notes in the vendor xlsx are wrong
  (vendor README, Python example, and a live COM9 test all agree).
- Written values are volatile without `SYSTEM_CONFIG_SAVE`.
- Direct UART wiring is 3.3 V; external UART chips need reverse-current protection.

## 9. Measured polling performance (COM9, `PdPower.Cli bench 300`)

| Command | min | avg | p95 | max |
|---|---|---|---|---|
| `READ_OUTPUT_DISPLAY` `0x85` | 0.14 | 0.23 | 0.30 | 0.67 |
| `READ_OUTPUT_STATE` `0x82` | 0.15 | 0.31 | 0.30 | 26.09 |
| `READ_INPUT_STATE` `0x8A` | 0.16 | 0.29 | 0.31 | 17.67 |
| **full poll cycle (ms)** | **0.48** | **0.83** | **0.85** | **26.58** |

0.3 % of a 250 ms period — transport is not the bottleneck (CDC data endpoints are bulk, hence
sub-millisecond). The real limits: the device refreshes its readings only every ~10 ms;
occasional 20–30 ms latency spikes; and frame tracing inflates a cycle ~15× when enabled.

Architecture: acquisition on a background `PeriodicTimer` loop (no UI contact); UI publish and
chart rendering batched at 60 ms. Measurement (`0x85`) every tick, state/input on a divisor —
keep the effective state period under 200 ms to catch the CC→OC transition of an OCP trip.
Measured CPU (one core): 2.2 % at 250 ms, 29.1 % at 10 ms, UI responsive at both.

The history ring buffer holds 14,400 points with storage interval = window / 14,400
(1m→4 ms, 5m→21 ms, 1h→250 ms) — short windows keep every sample, long ones thin themselves.
A freshly selected 1h window being mostly empty is normal.

## 10. Reconnect wait (USB drop recovery)

On a polling failure the app waits for the same port to return (**1 s × 60 attempts**): it
doesn't try to open until the name reappears in enumeration, then retries until `WHO_AM_I`
succeeds. **Serial numbers are compared** — a different device on the same port stops
auto-reconnect. On success, presets/OCP/brightness are re-read (a reboot reverts volatile
values to flash). The status chip shows `RECONNECT`, Trend history is preserved, and
`Disconnect` cancels.

Not yet exercised on hardware; test by replugging USB with output off, or sending
`SYSTEM_RESET` (0x40).

## 11. Measured OCP behavior (12 V, ~21 Ω load, `test-ocp 12.0 0.2`)

| | OCP OFF | OCP ON |
|---|---|---|
| Result | CC clamp, **output stays on** | **cut off after 220 ms** |
| Steady state | 4.15 V / 0.200 A (clamped exactly at the limit) | never reached |
| Final state | `ON` / `ConstantCurrent` | `OFF` / `OverCurrent` |

Measured facts not in the vendor docs: ① **OC is a latch** — cleared only by re-enabling the
output. ② The trip timer keys off **CC entry**, not the displayed current — under a hard
overload it trips before the reading ever reaches the limit. ③ The output voltage ramp is slow
(~640 ms from CC clamp to steady state).

Consequence: overload + OCP ON yields a brief low-voltage pulse then OFF, and 250 ms polling
can miss the ~200 ms CC phase — poll faster to see the cause of a trip.
