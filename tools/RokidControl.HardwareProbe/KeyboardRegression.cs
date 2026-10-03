using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RokidControl.App.Services;
using RokidControl.Core.Connections;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

internal static class KeyboardRegression
{
    public static async Task<int> RunAsync(string expectedSerial, string output)
    {
        Directory.CreateDirectory(output);
        var resources = AppResources.Locate();
        var adb = new AdbClient(resources.AdbPath, new ProcessRunner(resources.CreateEnvironment()));
        using var logger = new AppLogger(Path.Combine(output, "session.log"));
        var addressFile = Path.Combine(output, "wifi-address.txt");
        await File.WriteAllTextAsync(addressFile, expectedSerial);
        await using var connection = new RokidConnectionManager(adb, addressFile, resources.WatchdogPath);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = limit.Token;
        var results = new List<object>();
        var failures = new List<string>();
        string serial = expectedSerial;
        async Task<string> Shell(string command, CancellationToken token)
        {
            var result = await adb.RunAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(8), token);
            if (!result.Succeeded) throw new IOException(result.CombinedOutput);
            return result.Output.Trim();
        }
        void Pass(string name, object? details = null)
        {
            results.Add(new { Name = name, Details = details });
            Console.WriteLine("PASS " + name + (details is null ? "" : " " + JsonSerializer.Serialize(details)));
        }
        static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
        async Task<(string Label, bool Focused)> Selection()
        {
            await Task.Delay(200, ct);
            var xml = await Shell("CLASSPATH=/data/local/tmp/rokid_control_ui_reader.jar app_process / io.github.ksuzukigh.rokidcontrol.device.RokidUiReader apps", ct);
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            Require(document.Root?.Name == "apps", "No current app selection");
            return ((string?)document.Root!.Attribute("label") ?? "", (string?)document.Root.Attribute("focused") == "true");
        }
        async Task<int> Volume(CancellationToken token)
        {
            var text = await Shell("cmd media_session volume --stream 3 --get", token);
            var match = Regex.Match(text, @"volume is (\d+) in range");
            if (!match.Success) throw new IOException("Could not read volume");
            return int.Parse(match.Groups[1].Value);
        }
        int? originalVolume = null, originalBrightness = null;
        string? originalServices = null;
        try
        {
            serial = await connection.ConnectForStartupAsync(cancellationToken: ct);
            Require(serial == expectedSerial, "Unexpected test device");
            originalVolume = await Volume(ct);
            originalBrightness = int.Parse(await Shell("settings get system screen_brightness", ct));
            originalServices = await Shell("settings get secure enabled_accessibility_services", ct);
            await connection.StartWindowsModeAsync(ct);
            using var input = new PersistentAdbInputSession(resources.AdbPath, serial, resources.CreateEnvironment(), logger.Log);
            var keyboard = new KeyboardCommandProcessor(input, 480, 640, logger.Log);
            await keyboard.HandleAsync(KeyboardCommand.Applications, ct);
            var selected = await Selection();
            Require(selected.Focused, "Initial visible app does not have keyboard focus");
            Pass("initial app focus", selected.Label);
            for (var i = 0; !selected.Label.StartsWith("明るさ") && i < 25; i++)
            {
                await keyboard.HandleAsync(KeyboardCommand.Left, ct);
                selected = await Selection();
            }
            Require(selected.Label.StartsWith("明るさ"), "Could not reach the known first app");
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                await keyboard.HandleAsync(KeyboardCommand.Home, ct);
                await keyboard.HandleAsync(KeyboardCommand.Applications, ct);
                var before = await Selection();
                Require(before.Label.StartsWith("明るさ") && before.Focused, "Reopening A changed the displayed app or lost focus");
                await keyboard.HandleAsync(KeyboardCommand.Right, ct);
                var right = await Selection();
                Require(right.Label.StartsWith("音量") && right.Focused, "First Right did not select the immediate neighbor");
                await keyboard.HandleAsync(KeyboardCommand.Home, ct);
                await keyboard.HandleAsync(KeyboardCommand.Applications, ct);
                var reopened = await Selection();
                Require(reopened.Label.StartsWith("音量") && reopened.Focused, "Reopening A did not preserve the volume app");
                await keyboard.HandleAsync(KeyboardCommand.Left, ct);
                var left = await Selection();
                Require(left.Label.StartsWith("明るさ") && left.Focused, "First Left did not select the immediate neighbor");
                Pass("first arrows cycle " + cycle, new { Before = before.Label, Right = right.Label, Reopened = reopened.Label, Left = left.Label });
            }
            var distantLabels = new List<string> { (await Selection()).Label };
            for (var i = 0; i < 6; i++)
            {
                await keyboard.HandleAsync(KeyboardCommand.Right, ct);
                distantLabels.Add((await Selection()).Label);
            }
            await keyboard.HandleAsync(KeyboardCommand.Home, ct);
            await keyboard.HandleAsync(KeyboardCommand.Applications, ct);
            selected = await Selection();
            Require(selected.Label == distantLabels[^1] && selected.Focused, "Reopening A lost a distant selection");
            await keyboard.HandleAsync(KeyboardCommand.Left, ct);
            selected = await Selection();
            Require(selected.Label == distantLabels[^2], "First Left skipped a distant neighbor");
            Pass("distant app selection preserved", new { Before = distantLabels[^1], Left = selected.Label });
            for (var i = 0; i < 5; i++) { await keyboard.HandleAsync(KeyboardCommand.Left, ct); await Selection(); }
            Require((await Selection()).Label.StartsWith("明るさ"), "Did not return to brightness fixture");
            await keyboard.HandleAsync(KeyboardCommand.Home, ct);
            await keyboard.HandleAsync(KeyboardCommand.Applications, ct);
            await keyboard.HandleAsync(KeyboardCommand.Enter, ct);
            await Task.Delay(250, ct);
            var activity = await Shell("dumpsys activity activities", ct);
            Require(activity.Split('\n').Any(l => l.Contains("topResumedActivity=") && l.Contains(".page.brightness.SettingBrightnessActivity")), "First Enter did not open the displayed brightness app");
            Pass("first Enter opens displayed app");
            await keyboard.HandleAsync(KeyboardCommand.Right, ct);
            var brighter = int.Parse(await Shell("settings get system screen_brightness", ct));
            await keyboard.HandleAsync(KeyboardCommand.Left, ct);
            var restoredBrightness = int.Parse(await Shell("settings get system screen_brightness", ct));
            Require(brighter > originalBrightness && restoredBrightness == originalBrightness, "Brightness arrows did not increase and restore");
            Pass("brightness arrows", new { Before = originalBrightness, Right = brighter, Left = restoredBrightness });
            await keyboard.HandleAsync(KeyboardCommand.Back, ct);
            selected = await Selection();
            Require(selected.Label.StartsWith("明るさ"), "Esc did not return to the selected app");
            await keyboard.HandleAsync(KeyboardCommand.Right, ct);
            selected = await Selection();
            Require(selected.Label.StartsWith("音量"), "Esc did not retain app selection navigation");
            Pass("Esc retains selection");
            await keyboard.HandleAsync(KeyboardCommand.Home, ct);
            await Shell("am start -n com.rokid.os.sprite.launcher/.page.volume.SettingVolumeActivity", ct);
            await Task.Delay(250, ct);
            await keyboard.HandleAsync(KeyboardCommand.Right, ct);
            var louder = await Volume(ct);
            await keyboard.HandleAsync(KeyboardCommand.Left, ct);
            var restoredVolume = await Volume(ct);
            Require(louder > originalVolume && restoredVolume == originalVolume, "Volume arrows outside app selection did not increase and restore");
            Pass("volume arrows outside selection", new { Before = originalVolume, Right = louder, Left = restoredVolume });
            var refused = await adb.RunAsync(["-s", serial, "shell", "CLASSPATH=/data/local/tmp/rokid_control_ui_reader.jar app_process / io.github.ksuzukigh.rokidcontrol.device.RokidUiReader focus-apps"], TimeSpan.FromSeconds(8), ct);
            Require(!refused.Succeeded && refused.CombinedOutput.Contains("No unique visible launcher app"), "Focus helper did not refuse the settings screen");
            var unchanged = await Shell("dumpsys activity activities", ct);
            Require(unchanged.Split('\n').Any(l => l.Contains("topResumedActivity=") && l.Contains(".page.volume.SettingVolumeActivity"))
                && await Volume(ct) == originalVolume && int.Parse(await Shell("settings get system screen_brightness", ct)) == originalBrightness,
                "Refused focus operation changed the settings screen");
            Pass("focus helper refuses other screens without input");
            Require(await Shell("settings get secure enabled_accessibility_services", ct) == originalServices, "Accessibility service registration changed");
            var serviceState = await Shell("dumpsys accessibility", ct);
            // This firmware lists bound services by label and enabled services by component.
            Require(serviceState.Split('\n').Any(l => l.Contains("Bound services:") && l.Contains("label=R08 Access Bridge,")), "R08 service is not bound");
            Pass("R08 accessibility remains enabled and bound");
        }
        catch (Exception exception) { failures.Add(exception.ToString()); Console.WriteLine("FAIL " + exception.Message); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (originalVolume is int volume) await Shell($"cmd media_session volume --stream 3 --set {volume}", cleanup.Token);
                if (originalBrightness is int brightness) await Shell($"settings put system screen_brightness {brightness}", cleanup.Token);
                await Shell("input keyevent KEYCODE_BACK", cleanup.Token);
                await connection.StopWindowsModeAsync(cleanup.Token);
                Pass("cleanup", new { Volume = await Volume(cleanup.Token), Brightness = await Shell("settings get system screen_brightness", cleanup.Token), ScreenOffTimeout = await Shell("settings get system screen_off_timeout", cleanup.Token) });
            }
            catch (Exception exception) { failures.Add("Cleanup: " + exception.Message); }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Passed = failures.Count == 0, Results = results, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures.Count == 0 ? 0 : 1;
    }
}
