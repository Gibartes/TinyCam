using System.Diagnostics;
using System.Runtime.InteropServices;
using TinyCam.Models;
using TinyCam.Platform;

namespace TinyCam.Platform;

public sealed class UnixProcessGuardian : IProcessGuardian
{
    public ProcessStartInfo PrepareStartInfo(ProcessStartInfo psi, TinyCamConfig cfg)
    {
        if (!cfg.UseSetSidOnUnix) return psi;
        var setsid = (cfg.SetSidCandidates ?? new[] { "/usr/bin/setsid", "/bin/setsid" })
                     .FirstOrDefault(File.Exists);
        if (string.IsNullOrEmpty(setsid)) return psi;

        return new ProcessStartInfo
        {
            FileName = setsid,
            Arguments = $"-w -- {Escape(psi.FileName)} {psi.Arguments}",
            RedirectStandardOutput = psi.RedirectStandardOutput,
            RedirectStandardError = psi.RedirectStandardError,
            RedirectStandardInput = psi.RedirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    public void Attach(Process proc, TinyCamConfig cfg)
    {
    }

    public async Task<bool> TryGracefulTerminateAsync(Process proc, int timeoutMs)
    {
        try
        {
            int target = cfgCached?.UnixKillProcessGroup == true ? -Math.Abs(proc.Id) : proc.Id;
            _ = kill(target, SIGTERM);
            using var cts = new CancellationTokenSource(timeoutMs);
            await proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch { return false; }
    }

    public bool TryKill(Process proc, int waitMs)
    {
        try
        {
            int target = cfgCached?.UnixKillProcessGroup == true ? -Math.Abs(proc.Id) : proc.Id;
            _ = kill(target, SIGTERM);
            Thread.Sleep(600);
            _ = kill(target, SIGKILL);

            proc.WaitForExit(waitMs);
            return true;
        }
        catch { return false; }
    }

    private TinyCamConfig? cfgCached;
    public UnixProcessGuardian WithConfig(TinyCamConfig cfg) { cfgCached = cfg; return this; }

    private static string Escape(string s) =>
        s.Contains(' ') || s.Contains('"') || s.Contains('\'') ? $"\"{s.Replace("\"", "\\\"")}\"" : s;

    private const int SIGTERM = 15, SIGKILL = 9;
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
}
