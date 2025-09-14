using TinyCam.Models;

namespace TinyCam.Services;

public sealed class FileRetentionService : BackgroundService
{
    private readonly ILogger<FileRetentionService> _log;
    private readonly TinyCamConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRetentionService(ILogger<FileRetentionService> log, TinyCamConfig cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, _cfg.RetentionSweepSeconds / 3))), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnforceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "File retention sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _cfg.RetentionSweepSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    public async Task EnforceAsync(CancellationToken ct = default)
    {
        if (!_cfg.UseFileRotation || _cfg.RetainMaxFiles <= 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            var dir = _cfg.OutputDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            var now = DateTimeOffset.UtcNow;
            var safeWindow = TimeSpan.FromSeconds(Math.Max(0, _cfg.RetainSafeWindowSeconds));
            var prefix = _cfg.RetainFilePrefix ?? "camera_";
            var extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_cfg.RetainExtensions != null && _cfg.RetainExtensions.Length > 0)
                foreach (var e in _cfg.RetainExtensions) if (!string.IsNullOrWhiteSpace(e)) extSet.Add(e.TrimStart('.'));

            if (extSet.Count == 0) { extSet.Add("mp4"); extSet.Add("webm"); extSet.Add("mkv"); }

            var candidates = new List<FileInfo>();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!file.Contains(prefix)) continue;

                var ext = Path.GetExtension(file).TrimStart('.');
                if (!extSet.Contains(ext)) continue;

                try
                {
                    var fi = new FileInfo(file);
                    if (now - fi.LastWriteTimeUtc < safeWindow) continue;
                    candidates.Add(fi);
                }
                catch { /* ignore individual file errors */ }
            }

            if (candidates.Count <= _cfg.RetainMaxFiles) return;

            // 오래된 순으로 정렬 (마지막 수정시간 기준)
            candidates.Sort((a, b) => NullableDateTime(a.LastWriteTimeUtc).CompareTo(NullableDateTime(b.LastWriteTimeUtc)));

            var toDeleteCount = candidates.Count - _cfg.RetainMaxFiles;
            int deleted = 0, skipped = 0;

            foreach (var fi in candidates.Take(toDeleteCount))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    fi.Delete();
                    deleted++;
                }
                catch
                {
                    skipped++;
                }
            }

            if (deleted > 0 || skipped > 0)
            {
                _log.LogInformation("Retention sweep: total={Total}, keep={Keep}, deleted={Deleted}, skipped={Skipped}",
                    candidates.Count, _cfg.RetainMaxFiles, deleted, skipped);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTime NullableDateTime(DateTime dt) =>
        dt == default ? DateTime.MinValue : dt;
}

