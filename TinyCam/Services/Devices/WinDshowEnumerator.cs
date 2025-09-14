using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TinyCam.Models;

namespace TinyCam.Services.Devices;

public sealed class WinDshowTextEnumerator : IDeviceTextEnumerator
{
    private readonly TinyCamConfig _cfg;
    private readonly string _ffmpeg;

    public WinDshowTextEnumerator(TinyCamConfig cfg)
    {
        _cfg = cfg;
        _ffmpeg = string.IsNullOrWhiteSpace(cfg.FfmpegPath) ? "ffmpeg" : cfg.FfmpegPath;
    }

    public async Task<string> ListAsync(string kind, bool includeFormats, bool includeAlternativeName, CancellationToken ct = default)
    {
        try
        {
            var devs = await ProbeDevicesAsync(kind, includeAlternativeName, ct);
            var sb = new StringBuilder();
            sb.AppendLine("# TinyCam devices (Windows / dshow)");
            foreach (var d in devs)
            {
                sb.AppendLine($"[{d.Kind}] {d.Name}");
                if (!string.IsNullOrWhiteSpace(d.Alt))
                    sb.AppendLine($"- Alt : {d.Alt}");

                var recId = !string.IsNullOrWhiteSpace(d.Alt) ? d.Alt! : d.Name;
                sb.AppendLine($"- rec : {BuildToken(d.Kind, recId)}");
                sb.AppendLine($"- frd : {BuildToken(d.Kind, d.Name)}");

                if (includeFormats)
                {
                    var fmts = await ProbeFormatsAsync(d.Kind, d.Name, ct);
                    foreach (var f in fmts) sb.AppendLine($"    - {f}");
                }
            }
            return sb.ToString();
        }
        catch
        {
            return "# TinyCam devices (Windows / dshow)";
        }
    }

    public async Task<string> GetAsync(string identifier, string? kind, bool includeAlternativeName, CancellationToken ct = default)
    {
        string matchId = identifier;
        string? ioFromToken = null;

        if (TryParseToken(identifier, out var io, out var id))
        {
            ioFromToken = io;
            matchId = id;
        }

        try
        {
            bool needAlt = includeAlternativeName || IsAlt(matchId);
            var devs = await ProbeDevicesAsync(kind ?? "all", needAlt, ct);

            var dev = IsAlt(matchId)
                ? devs.FirstOrDefault(x =>
                    string.Equals(x.Alt ?? "", matchId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, matchId, StringComparison.OrdinalIgnoreCase))
                : devs.FirstOrDefault(x =>
                    string.Equals(x.Name, matchId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Alt ?? "", matchId, StringComparison.OrdinalIgnoreCase));

            if (dev is null) return "device not found";

            var sb = new StringBuilder();
            var useIo = ioFromToken ?? dev.Kind;
            sb.AppendLine("# TinyCam device (Windows / dshow)");
            sb.AppendLine($"Kind: {dev.Kind}");
            sb.AppendLine($"Name: {dev.Name}");
            if (!string.IsNullOrWhiteSpace(dev.Alt))
                sb.AppendLine($"Alt : {dev.Alt}");
            sb.AppendLine($"rec : {BuildToken(useIo, !string.IsNullOrWhiteSpace(dev.Alt) ? dev.Alt! : dev.Name)}");
            sb.AppendLine($"frd : {BuildToken(useIo, dev.Name)}");

            var fmts = await ProbeFormatsAsync(useIo, dev.Name, ct);
            if (fmts.Count > 0)
            {
                sb.AppendLine("Formats:");
                foreach (var f in fmts) sb.AppendLine($"  - {f}");
            }
            return sb.ToString();
        }
        catch
        {
            return "internal error";
        }
    }


    // ── 내부 구현 ────────────────────────────────────────────────
    private sealed record Dev(string Kind, string Name, string? Alt);

    private async Task<List<Dev>> ProbeDevicesAsync(string kind, bool includeAlt, CancellationToken ct)
    {
        var args = "-hide_banner -f dshow -list_devices true -i dummy";
        var (_, _, err) = await RunAsync(_ffmpeg, args, 8000, ct);

        var list = new List<Dev>();
        bool inVideo = false, inAudio = false;
        Dev? last = null;

        foreach (var raw in SplitLines(err))
        {
            var line = StripDshowPrefix(raw.Trim()); // [dshow @ ...]만 제거

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            { inVideo = true; inAudio = false; continue; }
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            { inVideo = false; inAudio = true; continue; }

            // Friendly: "Name" 또는 "Name" (video|audio)
            var mName = Regex.Match(line, "^\\s*\"(?<n>.+?)\"\\s*(\\((?<t>video|audio)\\))?\\s*$", RegexOptions.IgnoreCase);
            if (mName.Success)
            {
                var k = inVideo ? "video" : inAudio ? "audio"
                                : (mName.Groups["t"].Success ? mName.Groups["t"].Value.ToLowerInvariant() : "unknown");

                last = new Dev(k, mName.Groups["n"].Value, null);
                list.Add(last);
                continue;
            }

            // Alternative name:   Alternative name "..."
            if (includeAlt && last != null)
            {
                var mAlt = Regex.Match(line, "^\\s*Alternative name\\s+\"(?<a>.+)\"\\s*$", RegexOptions.IgnoreCase);
                if (mAlt.Success)
                {
                    var alt = mAlt.Groups["a"].Value;
                    last = last with { Alt = alt };
                    list[^1] = last;
                }
            }
        }
        return list;
    }

    private async Task<List<string>> ProbeFormatsAsync(string kind, string displayName, CancellationToken ct)
    {
        var io = kind.Equals("audio", StringComparison.OrdinalIgnoreCase) ? "audio" : "video";
        var args = $"-hide_banner -f dshow -list_options true -i {io}=\"{displayName}\"";
        var (_, _, err) = await RunAsync(_ffmpeg, args, 12000, ct);

        var list = new List<string>();
        foreach (var raw in SplitLines(err))
        {
            var line = StripDshowPrefix(raw).Trim(); // ★ 동일 처리
            var m = Regex.Match(line, "s=(?<w>\\d+)x(?<h>\\d+).*?fps=(?<f>[0-9.]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var pf = Regex.Match(line, "pixel_format=(?<p>[A-Za-z0-9_]+)");
                var w = m.Groups["w"].Value;
                var h = m.Groups["h"].Value;
                var f = m.Groups["f"].Value;
                var pxf = pf.Success ? pf.Groups["p"].Value : "?";
                list.Add($"{w}x{h}@{f} ({pxf})");
            }
        }
        return list;
    }

    // helpers
    private static bool TryParseToken(string s, out string io, out string id)
    {
        io = "video"; id = s;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Regex.Match(s.Trim(), "^(?<io>video|audio)\\s*=\\s*\"(?<id>.+)\"$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        io = m.Groups["io"].Value.ToLowerInvariant();
        id = m.Groups["id"].Value;
        return true;
    }
    private static bool IsAlt(string s) =>
        s.StartsWith("@device_pnp_", StringComparison.OrdinalIgnoreCase) ||
        s.IndexOf("@device_pnp_\\?\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
        s.IndexOf("@device_pnp_\\\\?\\", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string BuildToken(string kind, string id)
    {
        var io = kind.Equals("audio", StringComparison.OrdinalIgnoreCase) ? "audio" : "video";
        var quoted = id.Replace("\"", "\\\"");
        return $"{io}=\"{quoted}\"";
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

    private static string StripDshowPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s[0] != '[') return s;
        int i = s.IndexOf(']');
        if (i <= 0 || i + 1 >= s.Length) return s;
        return s.Substring(i + 1).TrimStart();
    }
}
