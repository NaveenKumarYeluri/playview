using System.Diagnostics;
using System.IO;

namespace PlayView.Services;

public record ScrcpyDevice(string Serial, string State, string Description);

public static class ScrcpyService
{
    public static string? FindScrcpy(string configured, string exeDir)
    {
        var candidates = new[]
        {
            configured,
            Path.Combine(exeDir, "tools", "scrcpy", "scrcpy.exe"),
            Path.Combine(exeDir, "scrcpy.exe"),
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        }

        return FindOnPath("scrcpy.exe");
    }

    public static string? FindAdb(string? scrcpyPath)
    {
        if (scrcpyPath != null)
        {
            var besideScrcpy = Path.Combine(Path.GetDirectoryName(scrcpyPath)!, "adb.exe");
            if (File.Exists(besideScrcpy)) return besideScrcpy;
        }
        return FindOnPath("adb.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<List<ScrcpyDevice>> ListDevicesAsync(string adbPath)
    {
        var output = await RunAdbAsync(adbPath, "devices -l");
        var devices = new List<ScrcpyDevice>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("List of devices") || line.StartsWith('*')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[1] is not ("device" or "unauthorized" or "offline")) continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..]
                ?.Replace('_', ' ');

            var description = parts[1] switch
            {
                "device" => model ?? parts[0],
                "unauthorized" => "unauthorized — accept the debugging prompt on the phone",
                _ => "offline",
            };

            devices.Add(new ScrcpyDevice(parts[0], parts[1], description));
        }

        return devices;
    }

    public static async Task<string> RunAdbAsync(string adbPath, string args)
    {
        var psi = new ProcessStartInfo(adbPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (stdout + stderr).Trim();
    }

    public static void Launch(string scrcpyPath, string? serial, string extraArgs)
    {
        var args = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial}";
        if (!string.IsNullOrWhiteSpace(extraArgs)) args += " " + extraArgs.Trim();

        Process.Start(new ProcessStartInfo(scrcpyPath, args.Trim())
        {
            UseShellExecute = false,
        });
    }
}
