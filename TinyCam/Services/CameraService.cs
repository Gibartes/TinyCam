using System.Diagnostics;
using TinyCam.Models;

namespace TinyCam.Services;

public class CameraService : BackgroundService
{
    private readonly ILogger<CameraService> _log;
    private TinyCamConfig _cfg;
    private readonly FFmpegMuxer _muxer;

    private Process? _proc;
    private CancellationTokenSource? _runnerCts;

    public bool IsRunning => _proc != null && !_proc.HasExited;

    public CameraService(ILogger<CameraService> log, TinyCamConfig cfg, FFmpegMuxer muxer)
    {
        _log = log; _cfg = cfg; _muxer = muxer;
    }

    public Task StartAsync(bool force = false)
    {
        if (IsRunning && !force) return Task.CompletedTask;
        _runnerCts?.Cancel();
        _runnerCts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoop(_runnerCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(bool graceful = true)
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                // Ask ffmpeg to stop nicely
                _proc.StandardInput.WriteLine("q");
                if (graceful)
                {
                    if (!await WaitForExitAsync(_proc, TimeSpan.FromSeconds(5)))
                    {
                        _proc.Kill(true);
                    }
                }
                else
                {
                    _proc.Kill(true);
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            _runnerCts?.Cancel();
        }
    }

    public Task ReloadConfigAsync(TinyCamConfig cfg)
    {
        _cfg = cfg;
        return StartAsync(force: true);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return StartAsync();
    }

    private async Task RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var psi = _muxer.BuildFFmpegStartInfo(DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogInformation("[ffmpeg] {line}", e.Data); };
                _proc.Start();
                _proc.BeginErrorReadLine();

                // Read stdout and broadcast in chunks
                var buffer = new byte[64 * 1024];
                while (!_proc.HasExited && !token.IsCancellationRequested)
                {
                    int n = await _proc.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (n <= 0) break;
                    _muxer.Broadcast(new ReadOnlyMemory<byte>(buffer, 0, n));
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RunLoop error, retry in 3s");
            }

            await Task.Delay(3000, token);
        }
    }

    private static Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        return Task.Run(() => p.WaitForExit((int)timeout.TotalMilliseconds));
    }
}
