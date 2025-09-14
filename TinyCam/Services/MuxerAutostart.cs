using TinyCam.Models;

namespace TinyCam.Services;

/*
 * On app startup, automatically start FFmpegMuxer. (Duplicate runs are prevented by MuxerHost itself.)
 * Set the environment variable TINY_CAM_AUTOSTART=0 or false to disable auto-start.
 */

public sealed class MuxerAutostart : IHostedService
{
    private readonly MuxerHost _host;
    private readonly TinyCamConfig _cfg;
    private readonly ILogger<MuxerAutostart> _log;

    public MuxerAutostart(MuxerHost host, TinyCamConfig cfg, ILogger<MuxerAutostart> log)
    {
        _host = host; _cfg = cfg; _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldAutostart())
        {
            _log.LogInformation("Muxer autostart disabled. Use POST /start to begin capture.");
            return;
        }

        _log.LogInformation("Starting FFmpeg muxer (autostart)...");
        await _host.StartAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsFalse(string? s) =>
        !string.IsNullOrEmpty(s) &&
        (s.Equals("0") || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase));

    private bool ShouldAutostart()
    {
        var env = Environment.GetEnvironmentVariable("TINY_CAM_AUTOSTART");
        if (IsFalse(env)) return false;

        var pi = _cfg.GetType().GetProperty("AutoStart");
        if (pi is not null && pi.PropertyType == typeof(bool))
            return (bool)(pi.GetValue(_cfg) ?? true);

        return true;
    }
}
