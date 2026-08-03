# WeactPD_PowerV1 — PC control tool for WeAct PD Power Mini V1 (Buck)

[한국어](README.md) · **English** (this document)

A **Windows 10 C# (WPF)** desktop application that controls and monitors the
[WeAct Studio PD Power Mini V1 BUCK](https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck)
programmable power supply.

> The Korean [README.md](README.md) is the primary document and carries the full measured data
> and design notes. This English version covers everything you need to build, use, and extend
> the project; where detail was condensed, the section numbers match the Korean original.

## 1. Overview

| Item | Detail |
|---|---|
| Target device | WeAct Studio PD Power Mini V1 BUCK (output 1–20 V / 0.05–3 A) |
| Transport | USB CDC virtual serial port (default) / UART (DP→TX, DM→RX, 3.3 V, CRC8) |
| Environment | Windows 10/11, Visual Studio 2022, .NET 8 (WPF) |
| Serial | `System.IO.Ports.SerialPort` |
| Design source | [`GUI/design/PD Power Tool Redesign.dc.html`](GUI/design/PD%20Power%20Tool%20Redesign.dc.html), spec: [`GUI/design/CSHARP-SPEC.md`](GUI/design/CSHARP-SPEC.md) |
| Protocol source | [`docs/protocol/`](docs/protocol/) (vendor xlsx ×2 + Python example) |
| AI control | Built-in MCP server — toggle in Setup, lets Claude and other AI clients read/control the device (§4) |

All read commands (WHO_AM_I, VERSION, SERIAL_NUM, OUTPUT_STATE, OUTPUT_ID, OUTPUT_DATA,
OUTPUT_DISPLAY, OCP_EN, OFFSET_EN, BRIGHTNESS, INPUT_STATE) and all write commands used by the
GUI have been verified against real hardware on COM9 (firmware `V1.0.2.0_6a997d9a`).

## 2. Protocol summary

### 2.1 Common rules

- The first byte of a frame is the command. **Read commands OR in `0x80`**
  (e.g. OUTPUT_DATA write `0x04`, read `0x84`).
- **USB CDC**: frames end with `0x0A`. Baud rate is irrelevant (virtual COM).
- **UART**: instead of the terminator, one **CRC8** byte (polynomial `0x31`, init `0xFF`,
  MSB-first). Baud 9600–460800 (device setting).
- Multi-byte values are **little-endian** (`l8` = low byte, `h8` = high byte).
- Responses share the structure: `[cmd(0x80|x)] [payload...] [0x0A or crc8]`.
- String responses (WHO_AM_I/VERSION/SERIAL) are `[cmd][ascii...][0x0A]` on USB CDC and
  `[cmd][length][ascii...][crc8]` on UART.

### 2.2 Write commands (PC → device)

| Command | Head | Payload | Notes |
|---|---|---|---|
| OUTPUT_EN | `0x02` | `x` | **`1 = enable`** (confirmed on hardware). The xlsx note claiming `0=enable` is wrong — see §8 |
| OUTPUT_ID | `0x03` | `x` | Preset group M0–M4 (0–4) |
| OUTPUT_DATA | `0x04` | `id, v_l8, v_h8, i_l8, i_h8` | Voltage in mV, current in mA |
| OUTPUT_OCP_EN | `0x06` | `x` | Over-current protection |
| OUTPUT_OFFSET_EN | `0x07` | `x` | Offset correction |
| BRIGHTNESS | `0x08` | `x` | 1–100 |
| OUTPUT_DISCHARGE_EN | `0x09` | `x` | Discharge function |
| INPUT_PD_VOLTAGE | `0x0A` | `v_l8, v_h8` | Unit 0.1 V, ≥ 8 V. Applied only while output OFF and Vout < 5 V |
| SYSTEM_RESET | `0x40` | — | |
| SYSTEM_CONFIG_SAVE | `0x44` | — | Persists the six items below at once (no individual selection) |
| SYSTEM_FACTORY_RESET | `0x45` | — | |

> Written values are **volatile** — lost on power cycle unless `SYSTEM_CONFIG_SAVE` (0x44)
> is sent (the "Save" button on the GUI Setup page).

### What SYSTEM_CONFIG_SAVE (`0x44`) persists

| Item | Command | Code |
|---|---|---|
| Active preset id | `OUTPUT_ID` | `0x03` |
| Preset voltage / current | `OUTPUT_DATA` | `0x04` |
| Over-current protection | `OUTPUT_OCP_EN` | `0x06` |
| Output offset correction | `OUTPUT_OFFSET_EN` | `0x07` |
| LCD brightness | `BRIGHTNESS` | `0x08` |
| PD request voltage | `INPUT_PD_VOLTAGE` | `0x0A` |

**Not persisted:**

- `OUTPUT_EN` (`0x02`) — output always starts OFF after a power cycle (safety behavior).
- `OUTPUT_DISCHARGE_EN` (`0x09`) — volatile *and* absent from the save list, so it
  **cannot be made permanent**; resend it after every connect.
- `SYSTEM_LCD_PANEL_TYPE` (`0x46`) — the opposite case: **non-volatile**, written
  immediately without `0x44`.

There is no command that reads back the flash contents. `READ_OUTPUT_DATA` etc. always return
the current effective (RAM) values, so the GUI's `UNSAVED` badge only tracks **changes this app
made since connecting** — knob changes on the device or the pre-connect state are unknowable.

### 2.3 Read commands (PC → device → response)

| Command | Head | Response payload | Notes |
|---|---|---|---|
| WHO_AM_I | `0x81` | `info(ascii)` | Device name |
| READ_OUTPUT_STATE | `0x82` | `x` | bit0: output en, bits2-1: 01=CC, 10=OC, 00=normal (CV) |
| READ_OUTPUT_ID | `0x83` | `x` | Current preset id |
| READ_OUTPUT_DATA | `0x84` | `id, v_l8, v_h8, i_l8, i_h8` | Request carries one `id` byte |
| READ_OUTPUT_DISPLAY | `0x85` | `v_l8, v_h8, i_l8, i_h8` | **Measured** voltage (mV) / current (mA) — used for polling |
| READ_OUTPUT_OCP_EN | `0x86` | `x` | |
| READ_OUTPUT_OFFSET_EN | `0x87` | `x` | |
| READ_BRIGHTNESS | `0x88` | `x` | |
| READ_INPUT_STATE | `0x8A` | `state, v_l8, v_h8, pv_l8, pv_h8` | state codes below; v = input voltage (mV), pv = PD request (0.1 V) |
| READ_SYSTEM_VERSION | `0xC2` | `version(ascii)` | |
| READ_SYSTEM_SERIAL_NUM | `0xC3` | `serial(ascii)` | |

**INPUT_STATE codes**: 0=WAIT, 1=WAIT_PD_OK, 2=WAIT_QC_OK, 3=ERR, 4=QC, 5=PD, 6=DC

### 2.4 CRC8 (UART mode only) — C# implementation

```csharp
public static byte Crc8(ReadOnlySpan<byte> data)
{
    byte crc = 0xFF;                    // initial value
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

The GUI follows an **in-house design**, not the vendor's example tool. The authoritative source
for dimensions and colors is the HTML mockup
[`GUI/design/PD Power Tool Redesign.dc.html`](GUI/design/PD%20Power%20Tool%20Redesign.dc.html);
[`GUI/design/CSHARP-SPEC.md`](GUI/design/CSHARP-SPEC.md) is a summary.
**Implementing from the summary alone gets the palette right but the structure wrong** —
always render the mockup. Key points:

- 1000×610 window, left rail (collapsible 196↔56 px) + two-tier main area
- **Rail**: Monitor/Setup/Log nav, preset cards M0–M4 (one-click apply, locked while output on),
  PD INPUT card (5/9/12/15/20 V), PORT card (COM select · connect)
- **Main**: three measurement cells (Voltage/Current/Power + set steppers, Output ON/OFF,
  CV/CC badge), dual-axis Trend chart, footer (SN · FW · input info)
- Steppers: wheel ±1 / Ctrl+wheel ±0.1, ranges 1–20 V / 0–3 A, applied to the device immediately
- Trend ranges 1m / 5m / 1h; poll interval adjustable in Setup in 10 ms steps (default 250 ms)

All stock WPF control templates (ComboBox, CheckBox, ListBox, buttons) are replaced in
[`Themes/Theme.xaml`](src/PdPower.App/Themes/Theme.xaml) — default Aero-era chrome mixed with
flat cards is what makes naive WPF ports look dated. Two hard-won rules: templates must take
`Background`/`BorderBrush` via `TemplateBinding` or caller `DataTrigger`s silently fail, and
hover states should modulate opacity rather than overwrite colors.

The Trend chart ([`Controls/TrendChart.cs`](src/PdPower.App/Controls/TrendChart.cs)) renders
directly: the x-axis is **time**, and when there are more points than pixels it draws per-column
min/max vertical lines (min/max decimation) — even resampling would erase exactly the spikes a
power-supply trace exists to show.

### Trend features

| Feature | Behavior |
|---|---|
| `1m` `5m` `1h` | Visible window; storage interval scales with it |
| `Auto` | Y-axis 0 → peak on 1/2/2.5/5 steps (default) |
| `Fit` | Y-axis hugs the data range — for observing ripple around e.g. 12.00 V |
| **Click on plot** | Freeze / resume toggle (no separate button) |
| **Wheel over an axis** | Zoom that axis: left half = voltage, right half = current |
| `CSV` | Exports the visible window (the frozen one if frozen) |
| Cursor | Hover reads the nearest sample: time · V · A · W · state |
| State band | Thin strip under the plot painting CV/CC/OC and output on/off over time |
| Stats line | min/avg/max of V·A·W for the visible window |

Freezing does **not** stop polling — it pins an immutable snapshot (`MeasurementWindow`)
so the ring buffer can keep overwriting behind it. Manual Y mode inherits the current visible
range (no jump) and zooms anchored at the cursor; `Auto`/`Fit` return from it. Frozen/manual
states are labeled with in-plot badges. CSV columns:
`timestamp,volts,amps,watts,regulation,output_enabled` (ISO 8601).

## 4. AI control — built-in MCP server

Enable **AI control server** in Setup and an MCP (Model Context Protocol) server starts inside
the app process, letting AI clients such as Claude read and control the device through this app.

### Why inside the app

A COM port can only be held by one process. A separate MCP server process could never reach the
device while the GUI is running, so **"use the app and let AI control it at the same time"
requires the server to live in the GUI process that owns the port.** It binds Streamable HTTP
to `http://localhost:5115` (localhost only, stateless) using the official C# SDK
([`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)).

Register the client (Claude Code, with the server enabled):

```bash
claude mcp add --transport http pdpower http://localhost:5115
```

### The 10 tools

| Tool | Kind | Action |
|---|---|---|
| `get_status` | read | connection, output on/off, CV/CC/OC, measured V/A/W, PD input, active preset & setpoint |
| `get_settings` | read | presets M0–M4, OCP, brightness, PD request voltage — **re-read from the device, so knob changes show up** |
| `get_history_stats` | read | min/avg/max of V/A/W over the visible Trend window |
| `set_output` | control | output on/off |
| `set_setpoint` | control | active preset voltage/current — either argument may be omitted |
| `select_preset` | control | switch preset — **refused while output is on** (same rule as the GUI) |
| `set_ocp` | control | over-current protection on/off |
| `set_pd_voltage` | control | PD request voltage (9/12/15/20 V, refused while output on) |
| `set_brightness` | control | LCD brightness 1–100 % (bypasses the slider debounce, written immediately) |
| `save_config` | control | `SYSTEM_CONFIG_SAVE` (0x44) |

### Structure and guardrails

- The gateway is [`MainViewModel.Mcp.cs`](src/PdPower.App/ViewModels/MainViewModel.Mcp.cs).
  Control requests are marshalled to the UI thread and run through **the same code paths as the
  GUI buttons** — screen state and the UNSAVED badge stay consistent, and device transactions
  serialize with the polling loop through the existing semaphore.
- Every AI command is logged on the Log page with an `[MCP]` prefix.
- Validation matches the GUI (voltage clamped to 1–20 V, current 0–3 A, PD standard steps only).
- The server is off by default and binds localhost only — unreachable from the network.
- Hardware-verified (2026-08-03, COM9): all 10 tools called, 12.004 V measured with output on,
  and both guardrails (preset switch while on, invalid PD voltage) refused with clear messages.

### MCP SDK 2.0 implementation notes

- **Nullable parameters also need a default value (`= null`) to become optional in the schema.**
  A bare `double? volts` is treated as required, and a call omitting it dies in binding before
  reaching the tool (observed: `set_setpoint {"volts":5}` failed with a generic error).
- Exceptions thrown by tools must be `McpException` for the client to see the message —
  the SDK masks everything else. The tool layer converts gateway exceptions.
- `FrameworkReference Microsoft.AspNetCore.App` flows to the App project, so the
  framework-dependent build now **also requires the ASP.NET Core Runtime**
  (standalone builds are unaffected).

## 5. Solution layout

```
WeactPD_PowerV1/
├─ WeactPD_PowerV1.sln
├─ Directory.Build.props          ← shared version (VersionPrefix)
├─ .githooks/pre-commit           ← bumps the patch version every commit
├─ .github/workflows/build.yml    ← build & test; Release deployment on tags
├─ README.md                      ← Korean original (this file: README.en.md)
├─ docs/protocol/                 ← vendor protocol sources (UART/USB xlsx, Python example)
├─ GUI/design/                    ← target GUI design (HTML mockup, C# spec, screenshots)
├─ GUI/icon/                      ← app icon sources (ico + png) — exe/window icons come from here
├─ src/
│  ├─ PdPower.Core/               ← protocol library (net8.0)
│  │  ├─ Protocol/                ←   PdCommand, ProtocolMode, Crc8, Frame
│  │  ├─ Models/                  ←   DeviceInfo, OutputStatus, InputStatus,
│  │  │                           ←   MeasurementHistory (time-based ring buffer)
│  │  ├─ PdPowerDevice.cs         ←   device API (SerialPort request/response)
│  │  └─ PdPowerException.cs
│  ├─ PdPower.Cli/                ← hardware verification console tool (net8.0)
│  ├─ PdPower.Mcp/                ← MCP server (tool definitions + Kestrel host, net8.0)
│  │  ├─ IPdPowerGateway.cs       ←   tools ↔ app interface + response DTOs
│  │  ├─ PdPowerMcpTools.cs       ←   the 10 tools exposed to AI
│  │  └─ McpServerHost.cs         ←   localhost:5115 Streamable HTTP host
│  └─ PdPower.App/                ← WPF GUI (net8.0-windows, MVVM)
│     ├─ Themes/Theme.xaml        ←   palette + full control template replacement
│     ├─ Controls/TrendChart.cs   ←   dual-axis time-series chart (direct rendering)
│     ├─ ViewModels/MainViewModel.cs      (+ MainViewModel.Mcp.cs — MCP gateway)
│     ├─ Converters.cs
│     └─ MainWindow.xaml
└─ tests/PdPower.Core.Tests/      ← xUnit ×42 — CRC8/frame/history
```

### Versioning · releases

The single source of truth is `VersionPrefix` in
[`Directory.Build.props`](Directory.Build.props). The app shows it under the rail logo;
the CLI shows it on the first line of `help`.

Enable the commit hook **once after cloning**:

```bash
git config core.hooksPath .githooks
```

[`.githooks/pre-commit`](.githooks/pre-commit) then bumps the patch digit into every commit.
Skip with `SKIP_VERSION_BUMP=1 git commit ...`; the hook steps aside during merge/rebase/cherry-pick.
(It is a *pre-commit* hook because at push time git has already fixed the outgoing SHAs —
a hook-created commit cannot ride along with that push.)

[`.github/workflows/build.yml`](.github/workflows/build.yml) decides the final version:

| Situation | Version | Output |
|---|---|---|
| push to `master` / PR | `<VersionPrefix>-dev.<run_number>` | Actions artifact |
| tag `v1.2.3` pushed | `1.2.3` | **GitHub Release created automatically** |

Release assets:

| File | Contents |
|---|---|
| `PdPowerTool.exe` | GUI, single exe (~1 MB; needs .NET 8 Desktop Runtime + **ASP.NET Core Runtime** — for the MCP server) |
| `PdPowerTool-standalone.exe` | GUI, self-contained (~70 MB, runs anywhere) |
| `PdPowerCli.exe` | CLI, single exe (needs .NET 8 Runtime) |
| `PdPowerCli-standalone.exe` | CLI, self-contained |

Releasing is just pushing a tag (CI runs the tests first, so a red test suite blocks the release):

```bash
git tag v0.2.0 && git push origin v0.2.0
```

### Build · run

```bash
dotnet build WeactPD_PowerV1.sln
```

```bash
dotnet test tests/PdPower.Core.Tests/PdPower.Core.Tests.csproj
```

Verify real hardware with the CLI (read-only):

```bash
dotnet run --project src/PdPower.Cli -- --port COM9 selftest
```

Run the WPF app:

```bash
dotnet run --project src/PdPower.App
```

### Implementation pitfalls

- Frames are sliced by fixed response length (`Frame.ResponseLength()`). **Scanning for the
  terminator (0x0A) cannot delimit frames** — payloads can contain 0x0A (e.g. 2570 mV =
  `0x0A0A`). Only ASCII responses use terminator scanning.
- The receive buffer is discarded right before each request to regain frame sync.
- `PdPowerDevice.FrameExchanged` fires **on a thread-pool thread** — subscribers must marshal
  to the dispatcher before touching UI collections.
- Continuously-changing controls (sliders) **need debouncing** — binding brightness directly
  would emit hundreds of frames per drag. The ViewModel coalesces writes over 250 ms and uses a
  suppression flag when filling the slider from a device read.
- `READ_INPUT_STATE` (0x8A) requires firmware **v1.0.2.0+** — treat failure as a normal path.

## 6. Roadmap

Done: protocol library with unit tests, hardware-verification CLI, the full mockup-faithful WPF
GUI (background polling loop with 10 ms capability, dual-axis Trend with measurement tooling,
Setup with OCP/brightness/save, USB auto-reconnect wait), built-in MCP server, app icons,
versioning + CI releases.

Open items: offset correction & discharge in Setup, reboot/factory-reset (behind a calibration
backup), rail collapse mode, inline preset editing, app-settings persistence, triggered burst
capture, power (W) series. See the Korean README §6 for the full annotated list.

## 7. References

- Vendor repository: <https://github.com/WeActStudio/WeActStudio.PDPowerMiniV1-Buck>
- Protocol Python example: [`docs/protocol/com_pdpower.py`](docs/protocol/com_pdpower.py)

## 8. Cautions

- `INPUT_PD_VOLTAGE` changes apply only while **output OFF and Vout < 5 V**.
- Continuous 3 A output needs extra cooling (2 A is fine continuously).
- **`*_EN` commands are `1 = enable` (hardware-confirmed).** The vendor xlsx repeats an
  incorrect "x=0,enable; x=1,disable" note on four commands; the vendor README, the Python
  example, and a live COM9 test all agree on `1 = enable`.
- Written values (presets, PD voltage, …) are volatile without `SYSTEM_CONFIG_SAVE`.
- Direct UART wiring is 3.3 V level; external UART chips need reverse-current protection.

## 9. Measured polling performance (COM9, `PdPower.Cli bench 300`)

Round-trip times of the three commands the GUI polls, in ms:

| Command | min | avg | p95 | max |
|---|---|---|---|---|
| `READ_OUTPUT_DISPLAY` `0x85` | 0.14 | 0.23 | 0.30 | 0.67 |
| `READ_OUTPUT_STATE` `0x82` | 0.15 | 0.31 | 0.30 | 26.09 |
| `READ_INPUT_STATE` `0x8A` | 0.16 | 0.29 | 0.31 | 17.67 |
| **full poll cycle** | **0.48** | **0.83** | **0.85** | **26.58** |

That is **0.3 % of a 250 ms period**. USB CDC data endpoints are bulk transfers, not bound to
the 1 ms USB frame schedule — hence sub-millisecond round trips. The real constraints when
shortening the period are: the device refreshes its displayed values only every ~10 ms;
occasional 20–30 ms latency spikes; and frame tracing (when enabled) inflates a cycle ~15×
through dispatcher marshalling — never benchmark with tracing on.

Architecture: acquisition runs on a background `PeriodicTimer` loop that only writes an
immutable snapshot + `MeasurementHistory`; the UI publishes at a fixed 60 ms; the chart
self-throttles rendering to 60 ms. Measured CPU on one core: 2.2 % at 250 ms, 29.1 % at 10 ms —
UI stays responsive at both.

The history is a time-based ring buffer capped at 14,400 points with
storage interval = window / 14,400, so short windows keep every polled sample and long windows
thin automatically (1m → 4 ms, 5m → 21 ms, 1h → 250 ms at max).

## 10. Reconnect wait (USB drop recovery)

When polling fails, the app does not drop the connection — it **waits for the same port to come
back** (1 s interval, up to 60 attempts). It does not even try to open the port until the name
reappears in enumeration, then retries until `WHO_AM_I` succeeds. The **serial number is
compared** — if a different device shows up on the same port name, auto-reconnect stops and
hands over to the user. On success, presets/OCP/brightness are re-read (a reboot reverts
volatile values to flash). During the wait the status chip shows `RECONNECT` and Trend history
is preserved; `Disconnect` cancels.

The drop-and-recover path itself has not been exercised on hardware yet; two ways to test:
unplug/replug USB with output off, or send `SYSTEM_RESET` (0x40).

## 11. Measured OCP behavior (12 V, ~21 Ω load, COM9)

With the current limit set below the load current (`PdPower.Cli test-ocp 12.0 0.2`):

| | OCP OFF | OCP ON |
|---|---|---|
| Result | CC clamp, **output stays on** | **cut off after 220 ms** |
| Steady state | 4.15 V / 0.200 A (clamped exactly at the limit) | never reached |
| Final state bits | `ON` / `ConstantCurrent` | `OFF` / `OverCurrent` |

Three facts not in the vendor docs, all measured:

1. **OC is a latch.** After a trip, the `OverCurrent` bit stays set through output-off and
   OCP-off; **re-enabling the output clears it.**
2. **The trip timer keys off CC entry, not the displayed current** — under a hard overload the
   device trips before the displayed current ever reaches the limit.
3. **The output voltage ramp is slow** (~640 ms from CC clamp to steady state) — short
   observation windows can misread it as "voltage too low".

Practical consequence: with overload + OCP ON you get a brief low-voltage pulse and then OFF,
and 250 ms polling can miss the ~200 ms CC phase entirely — poll faster (or use a trigger
capture) to see the cause of a trip.
