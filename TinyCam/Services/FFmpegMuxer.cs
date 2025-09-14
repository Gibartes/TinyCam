using System.Diagnostics;
using TinyCam.Models;
using TinyCam.Platform;
namespace TinyCam.Services;

public class FFmpegMuxer
{
    private readonly ILogger<FFmpegMuxer> _log;
    private readonly TinyCamConfig _cfg;

    // ── Subscriber management ────────────────────────────────────────────
    private readonly object _subLock = new();
    private int _subSeq = 0;
    private readonly Dictionary<int, Action<ReadOnlyMemory<byte>>> _subs = new();

    // ── Preroll (header) cache ── WebM: from EBML header ────────────────
    private readonly object _initLock = new();
    private byte[] _initBuf = Array.Empty<byte>();
    private int _initLen = 0;
    private const int InitCap = 2 * 1024 * 1024; // 2MB

    // WebM
    private static readonly byte[] EbmlMagic = { 0x1A, 0x45, 0xDF, 0xA3 };
    private static readonly byte[] ClusterMagic = { 0x1F, 0x43, 0xB6, 0x75 };
    private int _ebmlOffset = -1;
    private int _cluster0Offset = -1;

    // MP4
    private int _mp4InitEnd = -1;
    private bool _seenFtyp = false;


    // ── Running ffmpeg process handle ────────────────────────────────────
    private readonly object _procLock = new();
    private Process? _proc;               // current ffmpeg process
    private volatile bool _killedByUser;  // user-initiated kill flag (short-circuit restart delay)
    private readonly IProcessGuardian _guardian;

    public FFmpegMuxer(ILogger<FFmpegMuxer> log, TinyCamConfig cfg, IProcessGuardian guardian)
    {
        _log = log;
        _cfg = cfg;
        _guardian = guardian;
        Directory.CreateDirectory(_cfg.OutputDir);
    }

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> nee)
    {
        if (nee.Length == 0 || hay.Length < nee.Length) return -1;
        for (int i = 0; i <= hay.Length - nee.Length; i++)
            if (hay[i] == nee[0] && hay.Slice(i, nee.Length).SequenceEqual(nee)) return i;
        return -1;
    }

    private static uint BE32(ReadOnlySpan<byte> s)
        => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    private static ulong BE64(ReadOnlySpan<byte> s)
        => ((ulong)s[0] << 56) | ((ulong)s[1] << 48) | ((ulong)s[2] << 40) | ((ulong)s[3] << 32) |
           ((ulong)s[4] << 24) | ((ulong)s[5] << 16) | ((ulong)s[6] << 8) | (ulong)s[7];

    /*
     * Capture and accumulate initial bytes for preroll.
     * webm=true  → cache EBML..Tracks (stop before first Cluster)
     * webm=false → cache MP4 init segment (ftyp + moov)
     */
    public void CaptureInit(ReadOnlySpan<byte> chunk, bool webm)
    {
        lock (_initLock)
        {
            if (_initLen >= InitCap) return;
            if (_initBuf.Length == 0) _initBuf = new byte[InitCap];

            var toCopy = Math.Min(chunk.Length, InitCap - _initLen);
            if (toCopy <= 0) return;

            chunk[..toCopy].CopyTo(_initBuf.AsSpan(_initLen));
            _initLen += toCopy;

            var span = _initBuf.AsSpan(0, _initLen);

            if (webm)
            {
                if (_ebmlOffset < 0)
                {
                    var i = IndexOf(span, EbmlMagic);
                    if (i >= 0) _ebmlOffset = i;
                }
                if (_cluster0Offset < 0)
                {
                    var j = IndexOf(span, ClusterMagic);
                    if (j >= 0) _cluster0Offset = j;
                }
            }
            else
            {
                if (_mp4InitEnd >= 0) return;
                // ISO BMFF box parsing: ftyp + moov → Init segment
                int off = 0;
                while (off + 8 <= span.Length)
                {
                    uint size32 = BE32(span.Slice(off, 4));
                    uint type = BE32(span.Slice(off + 4, 4));
                    ulong size = size32;

                    if (size32 == 1)
                    {
                        if (off + 16 > span.Length) break;
                        size = BE64(span.Slice(off + 8, 8));
                        if (size < 16) break;                    // corrupted
                    }
                    else if (size32 < 8) break;                  // corrupted

                    if (off + (int)size > span.Length) break;

                    // 'ftyp' (0x66 74 79 70), 'moov' (0x6D 6F 6F 76)
                    if (type == 0x66747970) _seenFtyp = true;
                    if (_seenFtyp && type == 0x6D6F6F76)
                    {
                        _mp4InitEnd = off + (int)size; 
                        break;
                    }
                    off += (int)size;
                }
            }
        }
    }

    /*
     * Get the current preroll snapshot.
     * webm=true  → EBML..Tracks only (ends before first Cluster)
     * webm=false → exact MP4 init segment (ftyp+moov)
     */
    public ReadOnlyMemory<byte> GetInitSnapshot(bool webm)
    {
        lock (_initLock)
        {
            if (webm)
            {
                if (_initLen == 0 || _ebmlOffset < 0) return ReadOnlyMemory<byte>.Empty;
                var end = (_cluster0Offset > _ebmlOffset && _cluster0Offset <= _initLen) ? _cluster0Offset : _initLen;
                var len = end - _ebmlOffset;
                if (len <= 0) return ReadOnlyMemory<byte>.Empty;
                return new ReadOnlyMemory<byte>(_initBuf, _ebmlOffset, len); // EBML~Tracks까지만
            }
            else
            {
                if (_mp4InitEnd <= 0) return ReadOnlyMemory<byte>.Empty;     // ftyp+moov 아직 미완성
                return new ReadOnlyMemory<byte>(_initBuf, 0, _mp4InitEnd);   // 정확한 fMP4 Init segment
            }
        }
    }

    // Current running PID (null if none)
    public int? CurrentPid
    {
        get
        {
            lock (_procLock)
            {
                if (_proc is { HasExited: false }) return _proc.Id;
                return null;
            }
        }
    }

    // Subscribe / Unsubscribe / Broadcast
    public int Subscribe(Action<ReadOnlyMemory<byte>> onChunk)
    {
        lock (_subLock)
        {
            var id = ++_subSeq;
            _subs[id] = onChunk;
            return id;
        }
    }
    public void Unsubscribe(int id) { lock (_subLock) _subs.Remove(id); }
    public void Broadcast(ReadOnlyMemory<byte> chunk)
    {
        KeyValuePair<int, Action<ReadOnlyMemory<byte>>>[] targets;
        lock (_subLock) targets = _subs.ToArray();
        foreach (var kv in targets)
        {
            try { kv.Value(chunk); } catch { /* per-subscriber ignore */ }
        }
    }
    // Reset preroll state (called on ffmpeg restart)
    private void ResetInit()
    {
        lock (_initLock)
        {
            _initLen = 0;
            _ebmlOffset = -1;
            _cluster0Offset = -1;
            _mp4InitEnd = -1;
            _seenFtyp = false;
        }
    }
    // Build encoder argument string based on config / hardware
    private string HardwareAcceleration()
    {
        var g = _cfg.Gop > 0 ? $" -g {_cfg.Gop}" : "";
        var kmin = (_cfg.KeyintMin is > 0) ? $" -keyint_min {_cfg.KeyintMin}" : "";

        if (_cfg.Encoder == Encoder.qsv)
        {
            switch (_cfg.VideoCodec)
            {
                case Codec.vp9:
                    return $"-c:v vp9_qsv -global_quality {_cfg.GlobalQuality} " + $"{(_cfg.UseLowPower ? "-low_power 1 " : "")}" + g + kmin;
                case Codec.av1:
                    return $"-c:v av1_qsv -global_quality {_cfg.GlobalQuality} " + $"{(_cfg.UseLowPower ? "-low_power 1 " : "")}" + g + kmin;
                case Codec.h265:
                    return $"-c:v hevc_qsv {(_cfg.UseLowPower ? " -low_power 1" : "")} " + $" -b:v {_cfg.BitrateKbps}k -maxrate {_cfg.MaxrateKbps}k -bufsize {_cfg.BufsizeKbps}k" + g + kmin;
                case Codec.h264:
                default:
                    return $"-c:v h264_qsv {(_cfg.UseLowPower ? "-low_power 1 " : "")} " + $" -b:v {_cfg.BitrateKbps}k -maxrate {_cfg.MaxrateKbps}k -bufsize {_cfg.BufsizeKbps}k " + g + kmin;
            }
        }
        else if (_cfg.Encoder == Encoder.nvenc)
        {
            string tuneLL = _cfg.PipeLive ? " -tune ll" : "";
            string RcVbr(string codec) => $"-c:v {codec} -preset {_cfg.NvencPreset}{tuneLL} -rc vbr -b:v {_cfg.BitrateKbps}k -maxrate {_cfg.MaxrateKbps}k -bufsize {_cfg.BufsizeKbps}k{g}{kmin}";

            switch (_cfg.VideoCodec)
            {
                case Codec.av1:
                    return RcVbr("av1_nvenc") + " || " + RcVbr("h264_nvenc");
                case Codec.vp9: // NVENC does not support VP9 encoding
                    return RcVbr("h264_nvenc");
                case Codec.h265:
                    return  RcVbr("hevc_nvenc") + " -profile main";
                case Codec.h264:
                default:
                    return RcVbr("h264_nvenc");
            }
        }
        else
        {
            switch (_cfg.VideoCodec)
            {
                case Codec.vp9:
                    return $"-c:v libvpx-vp9 -crf {_cfg.GlobalQuality} -b:v 0" + " -row-mt 1 -threads 0" + " -deadline realtime" + g + kmin;
                case Codec.av1:
                    return $"-c:v libaom-av1 -crf {_cfg.GlobalQuality} -b:v 0" + " -cpu-used 6 -row-mt 1 -threads 0" + " -tiles 2x2" + g + kmin;
                case Codec.h265:
                    var _minKey = (_cfg.KeyintMin is > 0) ? _cfg.KeyintMin.Value : Math.Max(1, _cfg.Gop / 2);
                    return "-c:v libx265" + $" -preset veryfast -crf {_cfg.GlobalQuality}" + g + $" -x265-params keyint={Math.Max(1, _cfg.Gop)}:min-keyint={_minKey}:scenecut=0:tune=zerolatency";
                case Codec.h264:
                default:
                    var minKey = (_cfg.KeyintMin is > 0) ? _cfg.KeyintMin.Value : Math.Max(1, _cfg.Gop / 2);
                    return $"-c:v libx264 -preset veryfast -tune zerolatency -crf {_cfg.GlobalQuality}" + g + $" -x264-params keyint={Math.Max(1, _cfg.Gop)}:min-keyint={minKey}:scenecut=0 ";
            }
        }
    }

    /*
     * Build ffmpeg ProcessStartInfo (input/encoder/container/tee/pipe).
     * - StreamOnly: pipe only
     * - Otherwise: tee to (segment files | pipe)
     * - MP4 pipe → fMP4 (empty_moov + fragmented) for streaming compatibility
     * - WebM pipe → live tuning (cluster limits)
     */
    public ProcessStartInfo BuildFFmpegStartInfo(string nowStamp)
    {
        bool isWindows = _cfg.Platform == "windows"
                         || (_cfg.Platform == "auto" && Environment.OSVersion.Platform == PlatformID.Win32NT);

        // ---- Input configuration ----
        string input;
        if (isWindows)
        {
            var wc = _cfg.UseWallclockTimestamps ? "-use_wallclock_as_timestamps 1 " : "";
            var av = _cfg.EnableAudio && !string.IsNullOrWhiteSpace(_cfg.AudioDevice)
                     ? $"-i video=\"{_cfg.Device}\":audio=\"{_cfg.AudioDevice}\""
                     : $"-i video=\"{_cfg.Device}\"";

            input =
                $"-f dshow -rtbufsize {_cfg.RtbufSize} -thread_queue_size {_cfg.ThreadQueueSize} " +
                $"{wc}-framerate {_cfg.Fps} -video_size {_cfg.Width}x{_cfg.Height} {av}" +
                $"{(string.IsNullOrWhiteSpace(_cfg.ExtraInputArgs) ? "" : " " + _cfg.ExtraInputArgs)}";
        }
        else
        {
            var dev = _cfg.Device == "auto" ? "/dev/video0" : _cfg.Device;
            input =
                $"-f v4l2 -framerate {_cfg.Fps} -video_size {_cfg.Width}x{_cfg.Height} -i {dev}" +
                $"{(string.IsNullOrWhiteSpace(_cfg.ExtraInputArgs) ? "" : " " + _cfg.ExtraInputArgs)}";
        }

        // ---- Container auto selection ----
        string segFmt = "mp4";
        string pipeFmt = "mp4";
        if (_cfg.VideoCodec != Codec.h264 && _cfg.VideoCodec != Codec.h265)
        {
            segFmt = _cfg.SegmentFormat.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "webm" : _cfg.SegmentFormat.ToLowerInvariant();
            pipeFmt = _cfg.PipeFormat.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "webm" : _cfg.PipeFormat.ToLowerInvariant();
        }

        // ---- Video/Audio encoders ----
        string vEnc = HardwareAcceleration();
        if (!string.IsNullOrWhiteSpace(_cfg.ExtraEncoderArgs))
            vEnc += " " + _cfg.ExtraEncoderArgs;

        string maps = _cfg.EnableAudio ? "-map 0:v -map 0:a?" : "-map 0:v -an";
        string aEnc = "";
        if (_cfg.EnableAudio)
        {
            string aCodec = _cfg.AudioCodec.ToLowerInvariant() switch
            {
                "copy" => "copy",
                "aac" => "aac",
                "libopus" => "libopus",
                "auto" or "" => (segFmt == "mp4" || pipeFmt == "mp4") ? "aac" : "libopus",
                _ => "libopus"
            };
            aEnc = aCodec == "copy"
                ? "-c:a copy"
                : $"-c:a {aCodec} -b:a {_cfg.AudioBitrateKbps}k -ar {_cfg.AudioSampleRate} -ac {_cfg.AudioChannels}";
        }

        // ---- Compose final arguments ----
        string argsCommon =
            $"{input} {maps} {vEnc} {(string.IsNullOrWhiteSpace(aEnc) ? "" : aEnc + " ")}" +
            $"{(string.IsNullOrWhiteSpace(_cfg.ExtraOutputArgs) ? "" : _cfg.ExtraOutputArgs + " ")}";

        string args;

        if (_cfg.StreamOnly)
        {
            if (pipeFmt == "webm")
            {
                args = argsCommon +
                       $"-f webm -live 1 -cluster_time_limit {_cfg.ClusterTimeLimitMs} " +
                       $"-cluster_size_limit {_cfg.ClusterSizeLimitBytes} -";
            }
            else if (pipeFmt == "mp4")
            {
                args = argsCommon +
                       "-f mp4 -movflags +frag_keyframe+empty_moov+default_base_moof -";
            }
            else
            {
                args = argsCommon + $"-f {pipeFmt} -";
            }
        }
        else
        {
            // Tee: segment files + pipe
            int segSeconds = _cfg.SegmentUnit == "hour" ? _cfg.SegmentSeconds * 3600 : Math.Max(1, _cfg.SegmentSeconds);
            string ext = segFmt == "mp4" ? "mp4" : segFmt == "matroska" ? "mkv" : "webm";
            string outPattern = Path.Combine(_cfg.OutputDir, $"{_cfg.FileNamePattern}.{ext}").Replace("\\", "/");

            string segOpts = $"f=segment:segment_format={segFmt}:segment_time={segSeconds}:reset_timestamps=1:strftime=1";
            if (segFmt == "mp4") segOpts += ":segment_format_options=movflags=+faststart";
            string segmentTarget = $"[{segOpts}]{outPattern}";

            string pipeOpts = $"f={pipeFmt}";
            if (pipeFmt == "webm")
                pipeOpts += $":live=1:cluster_time_limit={_cfg.ClusterTimeLimitMs}:cluster_size_limit={_cfg.ClusterSizeLimitBytes}";
            else if (pipeFmt == "mp4")
                pipeOpts += ":movflags=+frag_keyframe+empty_moov+separate_moof+default_base_moof";
            string pipeTarget = $"[{pipeOpts}]pipe:1";

            string teeTargets = $"{segmentTarget}|{pipeTarget}";
            args = argsCommon + $"-f tee \"{teeTargets}\"";
        }

        if (_cfg.FfmpegDebug) _log.LogInformation("ffmpeg args: {args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = _cfg.FfmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi = _guardian.PrepareStartInfo(psi, _cfg);
        return psi;
    }

    /*
     * Main loop: (re)spawn ffmpeg, read stdout, cache preroll, broadcast live chunks.
     */
    public async Task RunProcessLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                ResetInit();
                var psi = BuildFFmpegStartInfo(DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                
                if (_cfg.FfmpegDebug)
                {
                    proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e?.Data)) _log.LogInformation("[ffmpeg] {line}", e!.Data); };
                }

                _proc = proc; _killedByUser = false;
                proc.Start();
                proc.BeginErrorReadLine();

                _guardian.Attach(proc, _cfg);

                var s = proc.StandardOutput.BaseStream;
                var buf = new byte[64 * 1024];

                bool option = !(_cfg.VideoCodec == Codec.h264 || _cfg.VideoCodec == Codec.h265);
                while (!proc.HasExited && !token.IsCancellationRequested)
                {
                    int n = await s.ReadAsync(buf.AsMemory(0, buf.Length), token);
                    if (n <= 0) break;
                    CaptureInit(buf.AsSpan(0, n), option);
                    Broadcast(new ReadOnlyMemory<byte>(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "FFmpeg loop error; will restart."); }
            finally { try { await Task.Delay(_killedByUser ? 200 : 3000, token); } catch { } _proc = null; }
        }
    }

    /*
     * Try graceful stop: send 'q' to ffmpeg stdin, then let guardian perform OS-level gentle termination.
     */

    public async Task<bool> TryGracefulQuitAsync(int timeoutMs = 3000)
    {
        var p = _proc;
        if (p is null || p.HasExited) return false;

        _killedByUser = true;

        // 1) App-level graceful stop (ffmpeg 'q'): container-safe close
        try { if (p.StartInfo.RedirectStandardInput) await p.StandardInput.WriteLineAsync("q"); } catch { }

        // 2) OS-level graceful stop is delegated to guardian (Unix: SIGTERM group / Windows: wait)
        return await _guardian.TryGracefulTerminateAsync(p, timeoutMs);
    }

    public bool TryKillProcess(int waitMs = 5000)
    {
        var p = _proc;
        if (p is null) return false;
        _killedByUser = true;
        return _guardian.TryKill(p, waitMs);
    }
}
