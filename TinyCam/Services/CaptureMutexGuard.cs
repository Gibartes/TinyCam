using System.Security.Cryptography;
using System.Text;
using TinyCam.Models;

namespace TinyCam.Services;

public sealed class CaptureMutexGuard : IHostedService, IDisposable
{
    private readonly TinyCamConfig _cfg;
    private readonly ILogger<CaptureMutexGuard> _log;
    private Mutex? _mutex;

    public CaptureMutexGuard(TinyCamConfig cfg, ILogger<CaptureMutexGuard> log)
    {
        _cfg = cfg; _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(_cfg.Device) ? "auto" : _cfg.Device;
        string hash;
        using (var sha = SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Substring(0, 16);

        bool created;
        _mutex = new Mutex(initiallyOwned: true, name: $@"Global\TinyCam_Capture_{hash}", createdNew: out created);
        if (!created)
        {
            _log.LogError("Another TinyCam instance is already capturing this device: {Device}", _cfg.Device);
            Environment.Exit(1);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _mutex?.Dispose(); } catch { }
    }
}
