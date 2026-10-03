using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RokidControl.Core.Processes;

namespace RokidControl.Core.Connections;

public sealed class R08Compatibility(IAdbClient adb, string resources)
{
    private const string Package = "com.anezium.r08accessbridge";
    private const string Service = Package + "/" + Package + ".RingControlAccessibilityService";
    private static readonly (string Path, string Hash, bool Restore)[] Helpers =
    [
        ("/data/local/tmp/r08-a11y-watchdog.sh", "fcc0b20b166c8bd0f653913f3b3a08b28b1928ba62b4f8dba9807d8db5f646db", false),
        ("/data/local/tmp/r08-shortcut-bridge.sh", "50e9e08693a1ad0208e7a231fc8cc7d0a410f7ea36c9b82711d87a3667b9f3f8", true),
    ];
    private Task<CommandResult> Run(string serial, string command, CancellationToken ct) =>
        adb.RunAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(5), ct);
    private async Task<string> Require(string serial, string command, CancellationToken ct)
    {
        var result = await Run(serial, command, ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException("R08の操作補助を復旧できませんでした。");
        return result.Output.Trim();
    }
    public async Task<bool> IsConfiguredAsync(string serial, CancellationToken ct)
    {
        var packages = await Run(serial, $"pm list packages {Package}", ct).ConfigureAwait(false);
        if (!packages.Succeeded || !packages.Output.Split('\n').Any(l => l.Trim() == $"package:{Package}")) return false;
        var services = await Run(serial, "settings get secure enabled_accessibility_services", ct).ConfigureAwait(false);
        var armed = await Run(serial, $"run-as {Package} cat shared_prefs/r08_bridge.xml", ct).ConfigureAwait(false);
        if (!services.Succeeded || !(services.Output.Trim().Split(':').Contains(Service) ||
            (armed.Succeeded && Regex.IsMatch(armed.Output, "<boolean\\s+name=\"bridge_armed\"\\s+value=\"true\"\\s*/>")))) return false;
        var nexus = await Run(serial, "test -f /data/local/tmp/rokid-nexus-a11y-watchdog.sh", ct).ConfigureAwait(false);
        if (nexus.TimedOut || nexus.ExitCode != 1) return false;
        foreach (var helper in Helpers)
        {
            var hash = await Run(serial, $"sha256sum {helper.Path}", ct).ConfigureAwait(false);
            if (!hash.Succeeded || hash.Output.Trim() != $"{helper.Hash}  {helper.Path}") return false;
        }
        return true;
    }
    private static bool IsProcess(string output, string path) => output.Split(['\0', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Contains(path);
    public async Task<bool> CloseLegacyListenerAsync(string serial, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(serial, ct).ConfigureAwait(false)) return false;
        var tls = await Require(serial, "getprop service.adb.tls.port", ct).ConfigureAwait(false);
        if (serial.Split(':').Last() != tls || tls == "5555") return false;
        var ports = new List<string>();
        foreach (var property in new[] { "service.adb.tcp.port", "persist.adb.tcp.port" })
            ports.Add(await Require(serial, $"getprop {property}", ct).ConfigureAwait(false));
        var active = ConnectionEncryption.ActiveListenerPorts(ports);
        if (active.Any(p => p != "5555")) return false;
        var addresses = await Require(serial, "getprop service.adb.listen_addrs", ct).ConfigureAwait(false);
        if (addresses is not ("" or "tcp:localhost:5555" or "tcp:127.0.0.1:5555")) return false;
        var sockets = await Require(serial, "ss -ltn", ct).ConfigureAwait(false);
        if (!sockets.Contains("Local Address:Port") || (active.Count == 0 && !Regex.IsMatch(sockets, @":5555\s"))) return false;
        await Require(serial, "setprop service.adb.tcp.port 0 && setprop persist.adb.tcp.port -1", ct).ConfigureAwait(false);
        if (addresses != "") await Require(serial, "setprop service.adb.listen_addrs ''", ct).ConfigureAwait(false);
        _ = await adb.RunAsync(["-s", serial, "usb"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        await Task.Delay(1000, ct).ConfigureAwait(false);
        return true;
    }
    public async Task RestoreAsync(string serial, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(serial, ct).ConfigureAwait(false)) return;
        var sockets = await Require(serial, "ss -ltn", ct).ConfigureAwait(false);
        if (!sockets.Contains("Local Address:Port") || Regex.IsMatch(sockets, @":5555\s")) throw new IOException("R08復旧前に旧接続の閉鎖を確認できません。");
        foreach (var helper in Helpers)
        {
            var status = await Run(serial, $"sh {helper.Path} status", ct).ConfigureAwait(false);
            if (!status.Succeeded && !(status.ExitCode == 1 && !status.TimedOut && status.Output.Trim().StartsWith("not running")))
                throw new IOException("R08の稼働状態を確認できません。");
            var match = Regex.Match(status.Output.Trim(), @"^running pid=([1-9][0-9]*)(?:\s|$)");
            var running = false;
            if (match.Success)
            {
                var command = await Require(serial, $"cat /proc/{match.Groups[1].Value}/cmdline", ct).ConfigureAwait(false);
                running = IsProcess(command, helper.Path);
                if (!running) await Require(serial, $"rm -f {helper.Path[..^3]}.pid", ct).ConfigureAwait(false);
            }
            if (!helper.Restore)
            {
                if (running)
                {
                    await Require(serial, $"sh {helper.Path} stop", ct).ConfigureAwait(false);
                    var after = await Run(serial, $"cat /proc/{match.Groups[1].Value}/cmdline", ct).ConfigureAwait(false);
                    if (after.TimedOut || (after.Succeeded && IsProcess(after.Output, helper.Path))) throw new IOException("R08の旧監視が停止しませんでした。");
                }
                continue;
            }
            if (running) continue;
            const string request = "/sdcard/Android/data/com.anezium.r08accessbridge/files/shortcut_bridge/request";
            await Require(serial, $"if [ -f '{request}' ] && [ ! -L '{request}' ]; then : > '{request}'; fi", ct).ConfigureAwait(false);
            await Require(serial, $"sh {helper.Path} start", ct).ConfigureAwait(false);
            var started = await Require(serial, $"sh {helper.Path} status", ct).ConfigureAwait(false);
            var pid = Regex.Match(started, @"^running pid=([1-9][0-9]*)(?:\s|$)");
            if (!pid.Success || !IsProcess(await Require(serial, $"cat /proc/{pid.Groups[1].Value}/cmdline", ct).ConfigureAwait(false), helper.Path))
                throw new IOException("R08の操作補助が起動しませんでした。");
        }
        var existing = await Require(serial, "settings get secure enabled_accessibility_services", ct).ConfigureAwait(false);
        var enabled = await Require(serial, "settings get secure accessibility_enabled", ct).ConfigureAwait(false);
        if (!existing.Split(':').Contains(Service) || enabled != "1")
        {
            var value = existing.Split(':').Contains(Service) ? existing : existing is "" or "null" ? Service : existing + ":" + Service;
            await Require(serial, $"settings put secure enabled_accessibility_services '{value.Replace("'", "'\\''")}' && settings put secure accessibility_enabled 1", ct).ConfigureAwait(false);
            if (!(await Require(serial, "settings get secure enabled_accessibility_services", ct).ConfigureAwait(false)).Split(':').Contains(Service) ||
                await Require(serial, "settings get secure accessibility_enabled", ct).ConfigureAwait(false) != "1") throw new IOException("R08の入力サービスを復旧できません。");
        }
        var info = await Require(serial, $"dumpsys package {Package}", ct).ConfigureAwait(false);
        if (!Regex.IsMatch(info, @"\bversionCode=36\b")) return;
        var uid = Regex.Match(await Require(serial, $"pm list packages -U {Package}", ct).ConfigureAwait(false), @"^package:com\.anezium\.r08accessbridge uid:([0-9]+)$");
        if (!uid.Success) throw new IOException("R08のUIDを確認できません。");
        const string remote = "/data/local/tmp/rokid_control_r08_direction.sh";
        var local = Path.Combine(resources, "rokid_r08_direction_bridge.sh");
        var expected = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false)));
        var currentPid = await Run(serial, "cat /data/local/tmp/rokid_control_r08_direction.pid", ct).ConfigureAwait(false);
        if (Regex.IsMatch(currentPid.Output.Trim(), @"^[1-9][0-9]*$"))
        {
            var process = await Run(serial, $"cat /proc/{currentPid.Output.Trim()}/cmdline", ct).ConfigureAwait(false);
            if (process.Succeeded && IsProcess(process.Output, remote))
            {
                if ((await Require(serial, $"sha256sum {remote}", ct).ConfigureAwait(false)).Split(' ')[0] != expected) throw new IOException("R08の橋渡し処理が一致しません。");
                return;
            }
        }
        var push = await adb.RunAsync(["-s", serial, "push", local, remote], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!push.Succeeded || (await Require(serial, $"sha256sum {remote}", ct).ConfigureAwait(false)).Split(' ')[0] != expected) throw new IOException("R08の橋渡し処理を準備できません。");
        await Require(serial, $"chmod 700 {remote} && sh {remote} start {uid.Groups[1].Value}", ct).ConfigureAwait(false);
    }
}
