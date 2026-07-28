using PdPower.Core;
using PdPower.Core.Models;
using PdPower.Core.Protocol;

namespace PdPower.Cli;

/// <summary>
/// PdPower.Core를 실장비로 검증하는 콘솔 도구.
/// 읽기 명령은 바로 실행되고, 출력 상태를 바꾸는 명령은 --yes 를 요구한다.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = CommandLine.Parse(args);
        if (options is null || options.Command is null or "help")
        {
            PrintUsage();
            return options?.Command is null ? 1 : 0;
        }

        if (options.Command == "ports")
        {
            foreach (var name in PdPowerDevice.GetPortNames()) Console.WriteLine(name);
            return 0;
        }

        using var device = new PdPowerDevice(options.Port, options.Mode, options.BaudRate);
        device.TimeoutMs = options.TimeoutMs;
        if (options.Trace) device.FrameExchanged += (_, trace) => Console.WriteLine($"  [{trace}]");

        try
        {
            device.Open();
            Console.WriteLine($"{options.Port} 연결 ({options.Mode}, {options.BaudRate} bps)\n");
            return await Dispatch(device, options).ConfigureAwait(false);
        }
        catch (PdPowerException ex)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"인자 오류: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> Dispatch(PdPowerDevice device, CommandLine options)
    {
        switch (options.Command)
        {
            case "info":
                await PrintInfo(device).ConfigureAwait(false);
                return 0;

            case "status":
                await PrintStatus(device).ConfigureAwait(false);
                return 0;

            case "presets":
                await PrintPresets(device).ConfigureAwait(false);
                return 0;

            case "monitor":
                await Monitor(device, options).ConfigureAwait(false);
                return 0;

            case "selftest":
                await PrintInfo(device).ConfigureAwait(false);
                Console.WriteLine();
                await PrintStatus(device).ConfigureAwait(false);
                Console.WriteLine();
                await PrintPresets(device).ConfigureAwait(false);
                return 0;

            case "preset":
            {
                int id = options.RequireInt(0, "preset id");
                await device.SetActivePresetIdAsync(id).ConfigureAwait(false);
                Console.WriteLine($"활성 프리셋 → M{await device.ReadActivePresetIdAsync().ConfigureAwait(false)}");
                return 0;
            }

            case "set":
            {
                int id = options.RequireInt(0, "preset id");
                double volts = options.RequireDouble(1, "volts");
                double amps = options.RequireDouble(2, "amps");
                await device.WritePresetAsync(id, volts, amps).ConfigureAwait(false);
                var readback = await device.ReadPresetAsync(id).ConfigureAwait(false);
                Console.WriteLine($"M{readback.PresetId} = {readback.Volts:F3} V / {readback.Amps:F3} A (읽기 확인)");
                Console.WriteLine("※ 전원 재인가 후에도 유지하려면 'save' 명령이 필요합니다.");
                return 0;
            }

            case "pd":
            {
                double volts = options.RequireDouble(0, "volts");
                if (!Confirm(options, $"PD 입력 전압 요청을 {volts:F1} V로 변경합니다.")) return 3;
                await device.SetPdRequestVoltageAsync(volts).ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false); // 재협상 대기
                var input = await device.ReadInputStatusAsync().ConfigureAwait(false);
                Console.WriteLine($"입력: {input.State}, {input.Volts:F3} V (요청 {input.RequestedPdVolts:F1} V)");
                Console.WriteLine("※ 출력 OFF + 출력전압 5 V 미만일 때만 반영됩니다.");
                return 0;
            }

            case "on":
            case "off":
            {
                bool enable = options.Command == "on";
                if (enable)
                {
                    var setpoint = await ReadActiveSetpoint(device).ConfigureAwait(false);
                    if (!Confirm(options, $"출력을 켭니다 — {setpoint.Volts:F3} V / {setpoint.Amps:F3} A 가 출력 단자에 인가됩니다.")) return 3;
                }
                await device.SetOutputEnabledAsync(enable).ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
                var status = await device.ReadOutputStatusAsync().ConfigureAwait(false);
                Console.WriteLine($"출력: {(status.Enabled ? "ON" : "OFF")} ({status.Regulation})");
                return 0;
            }

            case "save":
                await device.SaveConfigAsync().ConfigureAwait(false);
                Console.WriteLine("설정을 장치에 저장했습니다.");
                return 0;

            case "ocp":
            {
                string arg = options.RequireWord(0, "on|off");
                bool enable = arg.Equals("on", StringComparison.OrdinalIgnoreCase);
                await device.SetOcpEnabledAsync(enable).ConfigureAwait(false);
                Console.WriteLine($"OCP: {await device.ReadOcpEnabledAsync().ConfigureAwait(false)} (읽기 확인)");
                return 0;
            }

            case "bench":
                await Benchmark(device, options).ConfigureAwait(false);
                return 0;

            case "test-ocp":
                return await TestOcp(device, options).ConfigureAwait(false);

            case "probe-outputen":
                return await ProbeOutputEnablePolarity(device, options).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"알 수 없는 명령: {options.Command}");
                PrintUsage();
                return 1;
        }
    }

    private static async Task PrintInfo(PdPowerDevice device)
    {
        var info = await device.ReadDeviceInfoAsync().ConfigureAwait(false);
        Console.WriteLine($"장치명   : {info.Name}");
        Console.WriteLine($"펌웨어   : {info.FirmwareVersion}");
        Console.WriteLine($"시리얼   : {info.SerialNumber}");
    }

    private static async Task PrintStatus(PdPowerDevice device)
    {
        var status = await device.ReadOutputStatusAsync().ConfigureAwait(false);
        var measurement = await device.ReadMeasurementAsync().ConfigureAwait(false);
        var activeId = await device.ReadActivePresetIdAsync().ConfigureAwait(false);
        var setpoint = await device.ReadPresetAsync(activeId).ConfigureAwait(false);

        Console.WriteLine($"출력      : {(status.Enabled ? "ON" : "OFF")} / {status.Regulation} (raw 0x{status.Raw:X2})");
        Console.WriteLine($"실측      : {measurement.Volts:F3} V   {measurement.Amps:F3} A   {measurement.Watts:F3} W");
        Console.WriteLine($"활성설정  : M{activeId} = {setpoint.Volts:F3} V / {setpoint.Amps:F3} A");

        try
        {
            var input = await device.ReadInputStatusAsync().ConfigureAwait(false);
            Console.WriteLine($"입력      : {input.State} {input.Volts:F3} V (PD 요청 {input.RequestedPdVolts:F1} V)");
        }
        catch (PdPowerException ex)
        {
            // INPUT_STATE는 PD Power Mini V1 펌웨어 v1.0.2.0 이상에서만 지원된다.
            Console.WriteLine($"입력      : 조회 실패 ({ex.Message}) — 펌웨어 v1.0.2.0 이상 필요");
        }

        Console.WriteLine($"OCP       : {await device.ReadOcpEnabledAsync().ConfigureAwait(false)}");
        Console.WriteLine($"오프셋보정: {await device.ReadOffsetEnabledAsync().ConfigureAwait(false)}");
        Console.WriteLine($"밝기      : {await device.ReadBrightnessAsync().ConfigureAwait(false)}%");
    }

    private static async Task PrintPresets(PdPowerDevice device)
    {
        int activeId = await device.ReadActivePresetIdAsync().ConfigureAwait(false);
        Console.WriteLine("프리셋:");
        for (int id = 0; id < PdPowerDevice.PresetCount; id++)
        {
            var preset = await device.ReadPresetAsync(id).ConfigureAwait(false);
            string marker = id == activeId ? "◀ 활성" : "";
            Console.WriteLine($"  M{id}  {preset.Volts,7:F3} V  {preset.Amps,6:F3} A  {marker}");
        }
    }

    private static async Task Monitor(PdPowerDevice device, CommandLine options)
    {
        int intervalMs = options.OptionalInt(0) ?? 250;
        Console.WriteLine($"{intervalMs} ms 간격 폴링 — Ctrl+C 로 종료\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var measurement = await device.ReadMeasurementAsync(cts.Token).ConfigureAwait(false);
                var status = await device.ReadOutputStatusAsync(cts.Token).ConfigureAwait(false);
                Console.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff}  {measurement.Volts,7:F3} V  {measurement.Amps,6:F3} A  " +
                    $"{measurement.Watts,7:F3} W  {(status.Enabled ? "ON " : "OFF")}  {status.Regulation}");
                await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n중단됨.");
        }
    }

    /// <summary>
    /// GUI 폴링이 매 주기에 쓰는 읽기 명령들의 왕복 시간을 측정한다.
    /// 폴링 주기를 얼마까지 줄일 수 있는지 판단하는 근거 — 읽기 전용이라 출력 중에도 안전하다.
    /// </summary>
    private static async Task Benchmark(PdPowerDevice device, CommandLine options)
    {
        int iterations = options.OptionalInt(0) ?? 200;
        Console.WriteLine($"{iterations}회 반복 — GUI 폴링과 같은 3개 읽기 명령\n");

        var probes = new (string Name, Func<Task> Run)[]
        {
            ("READ_OUTPUT_DISPLAY 0x85", () => device.ReadMeasurementAsync()),
            ("READ_OUTPUT_STATE   0x82", () => device.ReadOutputStatusAsync()),
            ("READ_INPUT_STATE    0x8A", () => device.ReadInputStatusAsync()),
        };

        var samples = probes.ToDictionary(p => p.Name, _ => new List<double>(iterations));
        var cycle = new List<double>(iterations);
        var clock = new System.Diagnostics.Stopwatch();

        // 첫 왕복은 포트 워밍업이 섞이므로 버린다
        foreach (var probe in probes) await probe.Run().ConfigureAwait(false);

        for (int i = 0; i < iterations; i++)
        {
            double total = 0;
            foreach (var probe in probes)
            {
                clock.Restart();
                await probe.Run().ConfigureAwait(false);
                clock.Stop();
                double ms = clock.Elapsed.TotalMilliseconds;
                samples[probe.Name].Add(ms);
                total += ms;
            }
            cycle.Add(total);
        }

        Console.WriteLine("명령                        최소     평균     p95     최대");
        foreach (var probe in probes) PrintStats(probe.Name, samples[probe.Name]);
        Console.WriteLine(new string('-', 58));
        PrintStats("폴링 1주기 합계", cycle);

        double avgCycle = cycle.Average();
        Console.WriteLine($"\n250 ms 주기 기준 점유율 : {avgCycle / 250 * 100:F1} %");
        Console.WriteLine($"이론상 최소 주기        : 약 {cycle.Max():F1} ms (최악값 기준)");
    }

    private static void PrintStats(string label, List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double p95 = sorted[(int)(sorted.Count * 0.95)];
        Console.WriteLine($"{label,-26} {sorted[0],6:F2}  {values.Average(),6:F2}  {p95,6:F2}  {sorted[^1],6:F2}");
    }

    private static async Task<OutputSetpoint> ReadActiveSetpoint(PdPowerDevice device)
        => await device.ReadPresetAsync(await device.ReadActivePresetIdAsync().ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>
    /// OUTPUT_EN(0x02) 의 enable 값을 실장비로 확정한다.
    /// 제조사 xlsx(0=enable)와 Python 예제(1=enable)가 상충하므로 읽기 확인으로 판정한다.
    /// 출력 단자에 실제로 전압이 인가되므로 부하를 떼고 실행할 것.
    /// </summary>
    private static async Task<int> ProbeOutputEnablePolarity(PdPowerDevice device, CommandLine options)
    {
        var initial = await device.ReadOutputStatusAsync().ConfigureAwait(false);
        if (initial.Enabled)
        {
            Console.Error.WriteLine("출력이 이미 ON 입니다. OFF 상태에서 실행하세요.");
            return 3;
        }

        var setpoint = await ReadActiveSetpoint(device).ConfigureAwait(false);
        Console.WriteLine($"활성 설정: {setpoint.Volts:F3} V / {setpoint.Amps:F3} A");
        if (!Confirm(options,
                $"OUTPUT_EN 극성을 판정하려면 출력을 잠시 켜야 합니다 — {setpoint.Volts:F3} V 가 출력 단자에 인가됩니다. 부하를 분리하세요."))
            return 3;

        byte? onValue = null;
        foreach (byte candidate in new byte[] { 0x01, 0x00 })
        {
            device.OutputEnableOnValue = candidate;
            await device.SetOutputEnabledAsync(true).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            var probed = await device.ReadOutputStatusAsync().ConfigureAwait(false);
            Console.WriteLine($"  페이로드 0x{candidate:X2} → 출력 {(probed.Enabled ? "ON" : "OFF")}");

            // 판정 즉시 원래 상태(OFF)로 되돌린다.
            await device.SetOutputEnabledAsync(false).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);

            if (probed.Enabled) { onValue = candidate; break; }
        }

        var final = await device.ReadOutputStatusAsync().ConfigureAwait(false);
        Console.WriteLine($"\n복원 후 출력: {(final.Enabled ? "ON (주의: 수동으로 끄세요)" : "OFF")}");

        if (onValue is null)
        {
            Console.Error.WriteLine("어느 값으로도 출력이 켜지지 않았습니다 — 입력 전원/보호 상태를 확인하세요.");
            return 3;
        }

        Console.WriteLine($"판정: OutputEnableOnValue = 0x{onValue:X2}");
        Console.WriteLine(onValue == 0x01
            ? "→ Python 예제 기준(1=enable)이 맞습니다. Core 기본값 그대로 두면 됩니다."
            : "→ xlsx 문서 기준(0=enable)이 맞습니다. PdPowerDevice.OutputEnableOnValue 기본값을 0x00으로 바꾸세요.");
        return 0;
    }

    /// <summary>
    /// OCP 동작을 실측한다. 같은 전압/전류 설정에서 OCP off(→CC 물림)와 on(→트립)을 연달아 관찰해
    /// 문서에 없는 두 가지를 확인한다: 트립 시 output-en 비트가 0으로 떨어지는지, 래치인지 자동복구인지.
    /// 출력 단자에 실제로 전압이 인가되므로 부하를 알고 실행해야 한다.
    /// </summary>
    private static async Task<int> TestOcp(PdPowerDevice device, CommandLine options)
    {
        double volts = options.RequireDouble(0, "volts");
        double amps = options.RequireDouble(1, "amps");

        var initial = await device.ReadOutputStatusAsync().ConfigureAwait(false);
        int originalPresetId = await device.ReadActivePresetIdAsync().ConfigureAwait(false);
        var originalPreset = await device.ReadPresetAsync(originalPresetId).ConfigureAwait(false);
        bool originalOcp = await device.ReadOcpEnabledAsync().ConfigureAwait(false);

        Console.WriteLine($"현재 상태 : 출력 {(initial.Enabled ? "ON" : "OFF")}, OCP {originalOcp}");
        Console.WriteLine($"복원 대상 : M{originalPresetId} = {originalPreset.Volts:F3} V / {originalPreset.Amps:F3} A, OCP {originalOcp}");
        Console.WriteLine($"시험 조건 : {volts:F3} V / 전류 제한 {amps:F3} A → 부하 전류가 제한을 넘어야 트립\n");

        if (!Confirm(options, $"출력 단자에 {volts:F3} V 가 인가되고, 의도적으로 과전류 보호를 발동시킵니다."))
            return 3;

        try
        {
            // 항상 출력 OFF 에서 시작해 트립 시점을 명확히 본다
            await device.SetOutputEnabledAsync(false).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await device.WritePresetAsync(originalPresetId, volts, amps).ConfigureAwait(false);

            await RunOcpPhase(device, "1단계 · OCP OFF (전류 제한 = CC 물림 예상)", ocp: false).ConfigureAwait(false);
            await RunOcpPhase(device, "2단계 · OCP ON (약 200 ms 후 차단 예상)", ocp: true).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            // 시험 중 무슨 일이 있어도 출력을 끄고 원래 설정으로 되돌린다
            Console.WriteLine("\n복원 중…");
            await device.SetOutputEnabledAsync(false).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            await device.WritePresetAsync(originalPresetId, originalPreset.Volts, originalPreset.Amps).ConfigureAwait(false);
            await device.SetActivePresetIdAsync(originalPresetId).ConfigureAwait(false);
            await device.SetOcpEnabledAsync(originalOcp).ConfigureAwait(false);

            var restored = await device.ReadOutputStatusAsync().ConfigureAwait(false);
            var restoredPreset = await device.ReadPresetAsync(originalPresetId).ConfigureAwait(false);
            Console.WriteLine($"복원 완료 : 출력 {(restored.Enabled ? "ON ← 확인 필요" : "OFF")}, " +
                              $"M{restoredPreset.PresetId} = {restoredPreset.Volts:F3} V / {restoredPreset.Amps:F3} A, " +
                              $"OCP {await device.ReadOcpEnabledAsync().ConfigureAwait(false)}");

            // OC 는 래치라서 출력을 끄거나 OCP 를 꺼도 상태 비트에 남는다 (실측 확인).
            if (restored.Regulation == OutputRegulation.OverCurrent)
                Console.WriteLine("주의      : OC 래치가 남아 있습니다 — 출력을 다시 켜면 지워집니다.");
        }
    }

    /// <summary>출력을 켜고 상태 전이를 폴링으로 기록한다.</summary>
    private static async Task RunOcpPhase(PdPowerDevice device, string title, bool ocp)
    {
        const int WatchMs = 2500;
        const int SettleMs = 1500;

        // 장치 표시값 갱신보다 빠르게 읽으면 같은 값만 중복 수집되고 버스만 붐빈다.
        // 10 ms 면 200 ms 트립을 충분히 분해한다.
        const int SampleIntervalMs = 10;

        Console.WriteLine($"── {title} ──");
        await device.SetOcpEnabledAsync(ocp).ConfigureAwait(false);
        bool ocpReadback = await device.ReadOcpEnabledAsync().ConfigureAwait(false);
        Console.WriteLine($"OCP 설정 {ocp} → 읽기 확인 {ocpReadback}" +
                          (ocpReadback == ocp ? "" : "  ⚠ 불일치 — 극성 확인 필요"));

        var samples = new List<(long Ms, double V, double A, bool En, OutputRegulation Reg)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await device.SetOutputEnabledAsync(true).ConfigureAwait(false);

        long tripMs = -1;
        while (clock.ElapsedMilliseconds < WatchMs)
        {
            var measurement = await device.ReadMeasurementAsync().ConfigureAwait(false);
            var status = await device.ReadOutputStatusAsync().ConfigureAwait(false);
            long now = clock.ElapsedMilliseconds;
            samples.Add((now, measurement.Volts, measurement.Amps, status.Enabled, status.Regulation));

            // 첫 이상 징후(출력 꺼짐 또는 OC 상태)를 트립으로 본다
            if (tripMs < 0 && (!status.Enabled || status.Regulation == OutputRegulation.OverCurrent))
            {
                tripMs = now;
                Console.WriteLine($"  ▶ {now} ms — 출력 {(status.Enabled ? "ON" : "OFF")}, {status.Regulation}");
                break;
            }

            await Task.Delay(SampleIntervalMs).ConfigureAwait(false);
        }

        // 트립 후 자동 복구 여부 확인
        if (tripMs >= 0)
        {
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < SettleMs)
            {
                var measurement = await device.ReadMeasurementAsync().ConfigureAwait(false);
                var status = await device.ReadOutputStatusAsync().ConfigureAwait(false);
                samples.Add((tripMs + settle.ElapsedMilliseconds, measurement.Volts, measurement.Amps,
                            status.Enabled, status.Regulation));
                await Task.Delay(SampleIntervalMs).ConfigureAwait(false);
            }
        }

        PrintPhaseSummary(samples, tripMs);

        await device.SetOutputEnabledAsync(false).ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        Console.WriteLine();
    }

    private static void PrintPhaseSummary(
        List<(long Ms, double V, double A, bool En, OutputRegulation Reg)> samples, long tripMs)
    {
        if (samples.Count == 0) { Console.WriteLine("  샘플 없음"); return; }

        Console.WriteLine($"  샘플 {samples.Count}개, 평균 간격 {samples[^1].Ms / (double)samples.Count:F1} ms");
        Console.WriteLine("     t(ms)      V        A   출력  상태");

        // 상태 전이(◀)와 100 ms 간격 스냅샷을 함께 찍는다.
        // 전이만 찍으면 CC 물림 후의 정상상태 전압·전류가 안 보인다.
        (bool En, OutputRegulation Reg)? previous = null;
        long lastPrintedMs = long.MinValue;
        foreach (var s in samples)
        {
            bool changed = previous is null || previous.Value.En != s.En || previous.Value.Reg != s.Reg;
            bool periodic = s.Ms - lastPrintedMs >= 100;
            if (!changed && !periodic) continue;

            Console.WriteLine($"  {s.Ms,7}  {s.V,7:F3}  {s.A,7:F3}   {(s.En ? "ON " : "OFF")}  {s.Reg}" +
                              (changed ? "  ◀ 전이" : ""));
            previous = (s.En, s.Reg);
            lastPrintedMs = s.Ms;
        }

        var last = samples[^1];
        Console.WriteLine($"  최대 전류 {samples.Max(s => s.A):F3} A, 최대 전압 {samples.Max(s => s.V):F3} V");
        Console.WriteLine(tripMs >= 0
            ? $"  판정: {tripMs} ms 에 트립. 최종 출력 {(last.En ? "ON (자동 복구됨)" : "OFF (래치)")}, 최종 상태 {last.Reg}"
            : $"  판정: 트립 없음. 최종 출력 {(last.En ? "ON" : "OFF")}, 최종 상태 {last.Reg}");
    }

    private static bool Confirm(CommandLine options, string warning)
    {
        Console.WriteLine($"경고: {warning}");
        if (options.AssumeYes) return true;

        Console.Write("계속하려면 yes 입력: ");
        if (string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) return true;

        Console.WriteLine("취소했습니다.");
        return false;
    }

    private static void PrintUsage() => Console.WriteLine("""
        PdPower.Cli — WeAct PD Power Mini V1 실장비 검증 도구

        사용법:
          PdPower.Cli [옵션] <명령> [인자]

        읽기 전용 명령:
          ports                     사용 가능한 COM 포트 목록
          info                      장치명 / 펌웨어 / 시리얼
          status                    출력·실측·입력·보호 설정 요약
          presets                   프리셋 M0–M4 전체
          monitor [간격ms]          실측값 연속 폴링 (기본 250 ms)
          selftest                  info + status + presets 한 번에
          bench [횟수]              폴링 명령 왕복 시간 측정 (기본 200회)

        상태 변경 명령 (--yes 로 확인 생략):
          preset <id>               활성 프리셋 선택 (0–4)
          set <id> <V> <A>          프리셋 값 쓰기
          pd <V>                    PD 입력 전압 요청 (8 V 이상)
          on / off                  출력 on/off
          ocp on|off                과전류 보호 설정
          save                      휘발성 설정을 장치에 저장
          probe-outputen            OUTPUT_EN 극성 실측 판정
          test-ocp <V> <A>          OCP off/on 을 연달아 실측 (부하 필요, 시험 후 원복)

        옵션:
          --port <name>             기본 COM9
          --mode usb|uart           기본 usb (USB CDC)
          --baud <rate>             기본 115200 (UART 모드에서만 의미 있음)
          --timeout <ms>            기본 500
          --trace                   원시 프레임 hex 출력
          --yes                     확인 프롬프트 생략

        예:
          PdPower.Cli --port COM9 selftest
          PdPower.Cli --port COM9 --trace monitor 500
          PdPower.Cli --port COM9 set 0 5.0 1.5
        """);
}

/// <summary>의존성 없는 최소 인자 파서.</summary>
internal sealed class CommandLine
{
    public string Port { get; private set; } = "COM9";
    public ProtocolMode Mode { get; private set; } = ProtocolMode.UsbCdc;
    public int BaudRate { get; private set; } = 115200;
    public int TimeoutMs { get; private set; } = 500;
    public bool Trace { get; private set; }
    public bool AssumeYes { get; private set; }
    public string? Command { get; private set; }
    private readonly List<string> _positional = [];

    public static CommandLine? Parse(string[] args)
    {
        var result = new CommandLine();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--port": result.Port = Next(args, ref i, arg); break;
                case "--baud": result.BaudRate = int.Parse(Next(args, ref i, arg)); break;
                case "--timeout": result.TimeoutMs = int.Parse(Next(args, ref i, arg)); break;
                case "--trace": result.Trace = true; break;
                case "--yes" or "-y": result.AssumeYes = true; break;
                case "--mode":
                    string mode = Next(args, ref i, arg);
                    result.Mode = mode.ToLowerInvariant() switch
                    {
                        "usb" or "usbcdc" or "cdc" => ProtocolMode.UsbCdc,
                        "uart" => ProtocolMode.Uart,
                        _ => throw new ArgumentException($"--mode 값이 잘못됐습니다: {mode}"),
                    };
                    break;
                default:
                    if (arg.StartsWith('-')) { Console.Error.WriteLine($"알 수 없는 옵션: {arg}"); return null; }
                    if (result.Command is null) result.Command = arg;
                    else result._positional.Add(arg);
                    break;
            }
        }

        return result;
    }

    private static string Next(string[] args, ref int i, string option)
        => ++i < args.Length ? args[i] : throw new ArgumentException($"{option} 에 값이 필요합니다.");

    public int RequireInt(int index, string name)
        => int.TryParse(Require(index, name), out int value)
            ? value
            : throw new ArgumentException($"{name}: 정수여야 합니다.");

    public double RequireDouble(int index, string name)
        => double.TryParse(Require(index, name), out double value)
            ? value
            : throw new ArgumentException($"{name}: 숫자여야 합니다.");

    public int? OptionalInt(int index)
        => index < _positional.Count && int.TryParse(_positional[index], out int value) ? value : null;

    public string RequireWord(int index, string name) => Require(index, name);

    private string Require(int index, string name)
        => index < _positional.Count ? _positional[index] : throw new ArgumentException($"{name} 인자가 없습니다.");
}
