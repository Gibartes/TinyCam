using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TinyCam.Models;

namespace TinyCam.Services.Devices;

public sealed class UnixV4l2TextEnumerator : IDeviceTextEnumerator
{
    private readonly TinyCamConfig _cfg;
    private readonly string _ffmpeg;

    public UnixV4l2TextEnumerator(TinyCamConfig cfg)
    {
        _cfg = cfg;
        _ffmpeg = string.IsNullOrWhiteSpace(cfg.FfmpegPath) ? "ffmpeg" : cfg.FfmpegPath;
    }

    public async Task<string> ListAsync(string kind, bool includeFormats, bool includeAlternativeName, CancellationToken ct = default)
    {
        var devs = await ProbeDevicesAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("# TinyCam devices (Unix / v4l2)");
        foreach (var d in devs)
        {
            sb.AppendLine($"[video] {d.Name}");
            if (!string.IsNullOrWhiteSpace(d.Path))
                sb.AppendLine($"  path: {d.Path}");
            sb.AppendLine($"  rec : {d.Path ?? d.Name}");

            if (includeFormats && !string.IsNullOrWhiteSpace(d.Path))
            {
                var fmts = await ProbeFormatsAsync(d.Path!, ct);
                foreach (var f in fmts) sb.AppendLine($"    - {f}");
            }
        }
        return sb.ToString();
    }

    public async Task<string> GetAsync(string identifier, string? kind, bool includeAlternativeName, CancellationToken ct = default)
    {
        var devs = await ProbeDevicesAsync(ct);
        var dev = devs.FirstOrDefault(d =>
            string.Equals(d.Path ?? "", identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Name, identifier, StringComparison.OrdinalIgnoreCase));

        if (dev is null) return "device not found";

        var sb = new StringBuilder();
        sb.AppendLine("# TinyCam device (Unix / v4l2)");
        sb.AppendLine($"Kind: video");
        sb.AppendLine($"Name: {dev.Name}");
        if (!string.IsNullOrWhiteSpace(dev.Path))
            sb.AppendLine($"Path: {dev.Path}");
        sb.AppendLine($"rec : {dev.Path ?? dev.Name}");

        if (!string.IsNullOrWhiteSpace(dev.Path))
        {
            var fmts = await ProbeFormatsAsync(dev.Path!, ct);
            if (fmts.Count > 0)
            {
                sb.AppendLine("Formats:");
                foreach (var f in fmts) sb.AppendLine($"  - {f}");
            }
        }
        return sb.ToString();
    }

    // ── 내부 ────────────────────────────────────────────────────
    private sealed record Dev(string Name, string? Path);

    private async Task<List<Dev>> ProbeDevicesAsync(CancellationToken ct)
    {
        var list = new List<Dev>();
        var (ok, stdout, _) = await TryRunAsync("v4l2-ctl", "--list-devices", 4000, ct);
        if (ok && !string.IsNullOrWhiteSpace(stdout))
        {
            Dev? cur = null;
            foreach (var line in SplitLines(stdout))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("\t") && line.EndsWith(":"))
                {
                    cur = new Dev(line.TrimEnd(':'), null);
                    list.Add(cur);
                }
                else if (line.StartsWith("\t"))
                {
                    var path = line.Trim();
                    if (path.StartsWith("/dev/video"))
                    {
                        if (cur is null)
                        {
                            cur = new Dev(path, path);
                            list.Add(cur);
                        }
                        else
                        {
                            var idx = list.Count - 1;
                            list[idx] = cur with { Path = path };
                            cur = list[idx];
                        }
                    }
                }
            }
        }
        else
        {
            try
            {
                foreach (var path in Directory.GetFiles("/dev", "video*").OrderBy(s => s))
                    list.Add(new Dev(path, path));
            }
            catch { /* ignore */ }
        }
        return list;
    }

    private async Task<List<string>> ProbeFormatsAsync(string device, CancellationToken ct)
    {
        var args = $"-hide_banner -f v4l2 -list_formats all -i {device}";
        var (_, _, err) = await RunAsync(_ffmpeg, args, 12000, ct);

        var list = new List<string>();
        foreach (var raw in SplitLines(err))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, "(?<w>\\d+)x(?<h>\\d+).*?([(/ ](?<f>[0-9]+\\.?[0-9]*)\\s*fps\\)?)", RegexOptions.IgnoreCase);
            var pf = Regex.Match(line, "([A-Z0-9_]{3,5})\\s*(\\(|-|:)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var w = m.Groups["w"].Value;
                var h = m.Groups["h"].Value;
                var f = m.Groups["f"].Value;
                var pxf = pf.Success ? pf.Groups[1].Value : "?";
                list.Add($"{w}x{h}@{f} ({pxf})");
            }
        }
        return list;
    }

    // helpers
    private static async Task<(bool Ok, string Out, string Err)> TryRunAsync(string file, string args, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var (code, so, se) = await RunAsync(file, args, timeoutMs, ct);
            return (code == 0, so, se);
        }
        catch { return (false, "", ""); }
    }
    private static async Task<(int Exit, string Out, string Err)> RunAsync(string file, string args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var tOut = p.StandardOutput.ReadToEndAsync();
        var tErr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await Task.Run(() => p.WaitForExit(), cts.Token); } catch { }
        if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch { } }
        return (p.ExitCode, await tOut, await tErr);
    }
    private static IEnumerable<string> SplitLines(string s)
    {
        using var sr = new StringReader(s ?? "");
        string? line; while ((line = sr.ReadLine()) != null) yield return line;
    }
}
