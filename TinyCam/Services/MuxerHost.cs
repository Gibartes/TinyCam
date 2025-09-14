namespace TinyCam.Services;

public class MuxerHost
{
    private readonly FFmpegMuxer _muxer;
    private readonly ILogger<MuxerHost> _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public bool IsRunning => _loop is { IsCompleted: false };
    public int? CurrentPid => _muxer.CurrentPid;

    public Task StartAsync() => StartAsync(CancellationToken.None);
    public Task StopAsync() => StopAsync(CancellationToken.None);
    public MuxerHost(FFmpegMuxer muxer, ILogger<MuxerHost> log)
    {
        _muxer = muxer;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => _muxer.RunProcessLoop(_cts.Token), CancellationToken.None);
            _log.LogInformation("Muxer loop started.");
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("MuxerHost StopAsync: graceful ffmpeg shutdown…");
        try
        {
            var ok = await _muxer.TryGracefulQuitAsync(timeoutMs: 3000);
            if (!ok)
            {
                _log.LogInformation("Graceful quit failed — killing ffmpeg.");
                _muxer.TryKillProcess(waitMs: 3000);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error during ffmpeg shutdown — will continue stopping.");
        }

        CancellationTokenSource? cts; Task? loop;
        lock (_gate) { cts = _cts; loop = _loop; _cts = null; _loop = null; }
        if (cts != null) cts.Cancel();

        if (loop != null)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(5));
                await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, linked.Token));
            }
            catch { /* ignore */ }
        }

        _log.LogInformation("Muxer loop stopped.");
    }

    public async Task RestartAsync()
    {
        await StopAsync(CancellationToken.None);
        await StartAsync(CancellationToken.None);
    }

    public async Task KillAndRestartAsync(bool force = true)
    {
        bool ok = force
            ? _muxer.TryKillProcess()
            : await _muxer.TryGracefulQuitAsync();

        _log.LogInformation("Kill requested. force={force}, result={ok}", force, ok);
    }
}
