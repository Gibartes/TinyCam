using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using TinyCam.Data;

namespace TinyCam.Models;

public enum Codec { av1, vp9, h264, h265 }
public enum Encoder { cpu, qsv, nvenc }

public class TinyCamConfig
{
    public string Platform { get; set; } = "auto";
    public string Device { get; set; } = "auto";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public Codec VideoCodec { get; set; } = Codec.vp9;          // Supported Codec: vp9 av1 h264 h265
    public string OutputDir { get; set; } = "C:/recordings";    // Recommend storage: HDD
    public string SegmentUnit { get; set; } = "hour";
    public int SegmentSeconds { get; set; } = 3600;
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public bool FfmpegDebug { get; set; } = false;
    public Encoder Encoder { get; set; } = Encoder.qsv;         // Encoder: qsv (intel igpu), nvenc (nvidia), cpu
    public bool StreamOnly { get; set; } = false;
    public string NvencPreset { get; set; } = "p5";

    // Input stabilization
    public string RtbufSize { get; set; } = "512M";
    public int ThreadQueueSize { get; set; } = 1024;
    public bool UseWallclockTimestamps { get; set; } = true;
    public bool UseLowPower { get; set; } = true;

    // Encrypted configuration storage
    public bool SaveEncryptedConfig { get; set; } = false;
    public bool IsEncrypted { get; set; } = false;
    public string? Cipher { get; set; }

    // Live WebM tuning
    public int ClusterTimeLimitMs { get; set; } = 1000;
    public int ClusterSizeLimitBytes { get; set; } = 1048576;

    // GOP
    public int Gop { get; set; } = 60;
    public int? KeyintMin { get; set; } = 60;

    // === Newly added "Quality/Container/Extra Args" settings ===
    // QSV constant quality (AV1/VP9)
    public int GlobalQuality { get; set; } = 28;

    // H.264_QSV bitrate settings (kbps)
    public bool UseBitrate { get; set; } = false;
    public int BitrateKbps { get; set; } = 3000;
    public int MaxrateKbps { get; set; } = 3000;
    public int BufsizeKbps { get; set; } = 6000;

    // Container (auto/forced)
    // auto = av1/vp9 -> webm, h264 -> mp4 (file), mkv (pipe)
    public string SegmentFormat { get; set; } = "auto"; // webm|mp4|matroska|auto
    public string PipeFormat { get; set; } = "auto";    // webm|mp4|matroska|auto

    public bool PipeLive { get; set; } = true;          // Only applies when using webm
    public string FileNamePattern { get; set; } = "camera_%Y-%m-%d_%H-%M-%S";

    public bool EnableAudio { get; set; } = false;
    public string AudioDevice { get; set; } = "";
    public string AudioCodec { get; set; } = "auto";

    public int AudioBitrateKbps { get; set; } = 96;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;


    // Additional FFmpeg arguments (for advanced users)
    public string? ExtraInputArgs { get; set; }    // appended after the input block
    public string? ExtraEncoderArgs { get; set; }  // appended right after the codec
    public string? ExtraOutputArgs { get; set; }   // appended before the tee, shared across all outputs

    // Logging
    public string LogMode { get; set; } = "stdout";
    public string LogFile { get; set; } = "logs/tinycam.log";
    public int LogMaxSizeMB { get; set; } = 50;   // Max size per log file
    public int LogMaxFiles { get; set; } = 5;     // Number of retained files (tinycam.log.1 ~ .N)
    public bool LogRollDaily { get; set; } = false; // If true, start a new file when the date changes

    // Process Management (Requires restart)
    public bool UseJobObjectKillOnClose { get; set; } = true; // Windows only
    public bool UnixKillProcessGroup { get; set; } = true;    // Linux/macOS
    public bool UseSetSidOnUnix { get; set; } = true;         // Linux/macOS

    public string[] SetSidCandidates { get; set; } = new[] { "/usr/bin/setsid", "/bin/setsid" };


    // ── File rotation settings ───────────────────────────────────
    public bool UseFileRotation { get; set; } = true;      // Enable file rotation
    public int RetainMaxFiles { get; set; } = 60;
    public int RetentionSweepSeconds { get; set; } = 60;   // Rotation check interval (seconds)
    public int RetainSafeWindowSeconds { get; set; } = 60; // Files modified within this window are protected from deletion (considered in-progress)
    public string RetainFilePrefix { get; set; } = "camera_"; // Prefix of files subject to retention/deletion. Default: "camera_"
    public string[] RetainExtensions { get; set; } = new[] { "mp4", "webm", "mkv" }; // Target extensions for retention. Default: mp4, webm, mkv

    // ── SSL settings ─────────────────────────────────────────────
    public string? CertificateType { get; set; } = "pfx"; // "pfx" | "pem"
    // PFX
    public string? PfxPath { get; set; }
    public string? Password { get; set; }
    // PEM
    public string? PemCertPath { get; set; } // server.crt / fullchain.pem
    public string? PemKeyPath { get; set; } // server.key (PKCS#1/#8)

    public static TinyCamConfig Load(string path, KeyStore ks)
    {
        if (!File.Exists(path))
        {
            var def = new TinyCamConfig();
            Directory.CreateDirectory(def.OutputDir);
            Save(path, def, ks);
            return def;
        }
        try
        {
            var text = File.ReadAllText(path);
            if (text.TrimStart().StartsWith("ENC:"))
            {
                var b = Convert.FromBase64String(text.Trim().Substring(4));
                var plain = DecryptAesGcm(b, ks.AccessKeyBytes);
                text = System.Text.Encoding.UTF8.GetString(plain);
            }
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<TinyCamConfig>(text) ?? new TinyCamConfig();
        }
        catch
        {
            return new TinyCamConfig();
        }
    }

    public static TinyCamConfig ParseAndMaybeEncrypt(string yaml, KeyStore ks)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var cfg = deserializer.Deserialize<TinyCamConfig>(yaml) ?? new TinyCamConfig();
        return cfg;
    }

    public static void Save(string path, TinyCamConfig cfg, KeyStore ks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var yaml = serializer.Serialize(cfg);

        if (cfg.SaveEncryptedConfig)
        {
            var enc = EncryptAesGcm(System.Text.Encoding.UTF8.GetBytes(yaml), ks.AccessKeyBytes);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("ENC:" + Convert.ToBase64String(enc)));
        }
        else
        {
            File.WriteAllText(path, yaml);
        }
    }

    private static byte[] EncryptAesGcm(byte[] plain, byte[] key)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var g = new System.Security.Cryptography.AesGcm(key, 16);
        g.Encrypt(nonce, plain, cipher, tag);
        var output = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
        return output;
    }

    private static byte[] DecryptAesGcm(byte[] blob, byte[] key)
    {
        var nonce = blob.AsSpan(0, 12).ToArray();
        var tag = blob.AsSpan(12, 16).ToArray();
        var cipher = blob.AsSpan(28).ToArray();
        var plain = new byte[cipher.Length];
        using var g = new System.Security.Cryptography.AesGcm(key, 16);
        g.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
