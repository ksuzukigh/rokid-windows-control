using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RokidControl.Core.Connections;
using RokidControl.Core.Processes;

// A bounded, human-operated check. It never generates R08 inputs or asks for a retry.
internal static class RingCheck
{
    private const string BridgeLog = "/data/local/tmp/rokid_control_r08_direction.log";
    private sealed record Sample(DateTimeOffset At, string Phase, int Volume, int Brightness, string[] Events);

    public static async Task<int> RunAsync(string adbPath, string serial, string output, bool prepareOnly)
    {
        var adb = new AdbClient(adbPath, new ProcessRunner());
        Directory.CreateDirectory(output);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var ct = limit.Token;
        async Task<string> Shell(string command, CancellationToken token)
        {
            var result = await adb.RunAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(3), token);
            if (!result.Succeeded) throw new IOException("Device command did not complete: " + command.Split(';')[0]);
            return result.Output;
        }
        async Task<Sample> Read(string phase, CancellationToken token)
        {
            var data = await Shell($"cmd media_session volume --stream 3 --get; echo BRIGHTNESS; settings get system screen_brightness; tail -30 {BridgeLog}", token);
            var volume = Regex.Match(data, @"volume is (\d+) in range");
            var brightness = Regex.Match(data, @"BRIGHTNESS\r?\n(\d+)");
            if (!volume.Success || !brightness.Success) throw new IOException("Could not read adjustment values");
            return new Sample(DateTimeOffset.Now, phase, int.Parse(volume.Groups[1].Value), int.Parse(brightness.Groups[1].Value),
                data.Split('\n').Select(l => l.Trim()).Where(l => Regex.IsMatch(l, @"^\d+ setting key=(21|22) source_stamp=\d+\.\d+$")).ToArray());
        }

        var running = await Shell("sh /data/local/tmp/rokid_control_r08_direction.sh status", ct);
        if (!Regex.IsMatch(running, @"^running pid=\d+ version=1")) throw new IOException("Direction bridge is not running");
        var input = await Shell("dumpsys input", ct);
        if (!input.Contains("R08_")) throw new IOException("R08 is not connected");
        var tls = (await Shell("getprop service.adb.tls.port", ct)).Trim();
        if (!serial.EndsWith(":" + tls) || tls == "5555" || !int.TryParse(tls, out _)) throw new IOException("Connection is not the advertised TLS endpoint");
        var plaintext = (await Shell("getprop service.adb.tcp.port; getprop persist.adb.tcp.port", ct)).Split('\n');
        if (ConnectionEncryption.ActiveListenerPorts(plaintext).Count != 0) throw new IOException("A legacy listener remains");
        var baseline = await Read("baseline", ct);
        if (baseline.Volume >= 15 || baseline.Brightness >= 255) throw new IOException("Adjustment is already at maximum");
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(new { At = DateTimeOffset.Now, serial, baseline, RingConnected = true, BridgeRunning = true, DurationSeconds = 40 }, options), ct);
        if (prepareOnly) { Console.WriteLine("READY: TLS, R08, bridge and readable adjustment values verified. No user input needed yet."); return 0; }

        var samples = new List<Sample> { baseline };
        var completed = new Dictionary<string, bool>();
        var seen = baseline.Events.ToHashSet();
        var status = "incomplete";
        var usbAbsent = false;
        string? error = null;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            foreach (var phase in new[] { "volume", "brightness" })
            {
                var activity = phase == "volume" ? "volume.SettingVolumeActivity" : "brightness.SettingBrightnessActivity";
                await Shell($"input keyevent KEYCODE_WAKEUP; am start -n com.rokid.os.sprite.launcher/.page.{activity}", ct);
                var accepted = new List<int>();
                Console.WriteLine("READY " + phase);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var sample = await Read(phase, ct);
                    samples.Add(sample);
                    foreach (var line in sample.Events)
                    {
                        if (!seen.Add(line)) continue;
                        accepted.Add(int.Parse(Regex.Match(line, @"key=(21|22)").Groups[1].Value, CultureInfo.InvariantCulture));
                    }
                    if (accepted.Count >= 2) break;
                    await Task.Delay(200, ct);
                }
                var phaseSamples = samples.Where(s => s.Phase == phase).ToArray();
                var changed = phase == "volume" ? phaseSamples.Any(s => s.Volume > baseline.Volume) : phaseSamples.Any(s => s.Brightness > baseline.Brightness);
                var restored = phaseSamples.Length > 0 && (phase == "volume" ? phaseSamples[^1].Volume == baseline.Volume : phaseSamples[^1].Brightness == baseline.Brightness);
                completed[phase] = accepted.SequenceEqual(new[] { 22, 21 }) && changed && restored;
                Console.WriteLine($"RESULT {phase}: {completed[phase]} (accepted={string.Join(',', accepted)}, changed={changed}, restored={restored})");
                if (!completed[phase]) break;
            }
            var devices = await adb.RunAsync(["devices"], TimeSpan.FromSeconds(3), ct);
            if (!devices.Succeeded) throw new IOException("Could not verify USB absence");
            usbAbsent = !AdbParsers.ParseDevices(devices.Output).Any(d => d.IsReady && d.IsUsb);
            status = completed.Count == 2 && completed.Values.All(v => v) && usbAbsent ? "passed" : "incomplete";
        }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                await Shell($"cmd media_session volume --stream 3 --set {baseline.Volume}; settings put system screen_brightness {baseline.Brightness}; input keyevent KEYCODE_BACK", cleanup.Token);
            }
            catch (Exception exception) { error = (error ?? "") + " Cleanup: " + exception.Message; status = "incomplete"; }
            var report = new { At = DateTimeOffset.Now, status, usbAbsent, completed, error, samples };
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, options));
            Console.WriteLine("DONE: user participation ends here. " + status);
        }
        return status == "passed" ? 0 : 1;
    }
}
