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
    public Codec VideoCodec { get; set; } = Codec.vp9;
    public string OutputDir { get; set; } = "recordings";
    public string SegmentUnit { get; set; } = "hour";
    public int SegmentSeconds { get; set; } = 3600;
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public bool FfmpegDebug { get; set; } = false;
    public Encoder Encoder { get; set; } = Encoder.qsv;
    public bool StreamOnly { get; set; } = false;
    public string NvencPreset { get; set; } = "p5";

    // 입력 안정화
    public string RtbufSize { get; set; } = "512M";
    public int ThreadQueueSize { get; set; } = 1024;
    public bool UseWallclockTimestamps { get; set; } = true;
    public bool UseLowPower { get; set; } = true;

    // 암호화 저장 등
    public bool SaveEncryptedConfig { get; set; } = false;
    public bool IsEncrypted { get; set; } = false;
    public string? Cipher { get; set; }

    // 라이브 webm 튜닝
    public int ClusterTimeLimitMs { get; set; } = 1000;
    public int ClusterSizeLimitBytes { get; set; } = 1048576;

    // GOP
    public int Gop { get; set; } = 60;
    public int? KeyintMin { get; set; } = 60;

    // === 새로 추가된 “품질/컨테이너/추가 인자” 설정 ===
    // QSV 고정 퀄리티(AV1/VP9)
    public int GlobalQuality { get; set; } = 28;

    // H.264_QSV 비트레이트(kbps)
    public int BitrateKbps { get; set; } = 3000;
    public int MaxrateKbps { get; set; } = 3000;
    public int BufsizeKbps { get; set; } = 6000;

    // 컨테이너(자동/강제)
    // auto = av1/vp9 -> webm, h264 -> mp4(파일), mkv(파이프)
    public string SegmentFormat { get; set; } = "auto"; // webm|mp4|matroska|auto
    public string PipeFormat { get; set; } = "auto";    // webm|mp4|matroska|auto

    public bool PipeLive { get; set; } = true;          // webm일 때만 적용
    public string FileNamePattern { get; set; } = "camera_%Y-%m-%d_%H-%M-%S";

    public bool EnableAudio { get; set; } = false;
    public string AudioDevice { get; set; } = "";
    public string AudioCodec { get; set; } = "auto";

    public int AudioBitrateKbps { get; set; } = 96;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;


    // 추가로 얹고 싶은 ffmpeg 인자(전문가용)
    public string? ExtraInputArgs { get; set; }    // 입력 블록 뒤에
    public string? ExtraEncoderArgs { get; set; }  // 코덱 뒤에
    public string? ExtraOutputArgs { get; set; }   // tee 앞 전체 출력에 공통으로

    // Logging
    public string LogMode { get; set; } = "stdout";
    public string LogFile { get; set; } = "logs/tinycam.log";
    public int LogMaxSizeMB { get; set; } = 50;   // 파일 하나 최대 크기
    public int LogMaxFiles { get; set; } = 5;     // 보관 개수 (tinycam.log.1 ~ .N)
    public bool LogRollDaily { get; set; } = false; // true면 날짜 바뀌면 새 파일 시작

    // Process Management (Required restart)
    public bool UseJobObjectKillOnClose { get; set; } = true; // Windows
    public bool UnixKillProcessGroup { get; set; } = true;    // Linux/macOS
    public bool UseSetSidOnUnix { get; set; } = true;         // Linux/macOS

    public string[] SetSidCandidates { get; set; } = new[] { "/usr/bin/setsid", "/bin/setsid" };


    // ── File rotation settings ───────────────────────────────────
    public bool UseFileRotation { get; set; } = true;      // 파일 로테이션 활성화
    public int RetainMaxFiles { get; set; } = 60;
    public int RetentionSweepSeconds { get; set; } = 60;   // 로테이션 검사 주기(초)
    public int RetainSafeWindowSeconds { get; set; } = 60; // 마지막 수정 후 이 초 이내의 파일은 삭제 대상에서 제외(진행 중 보호)
    public string RetainFilePrefix { get; set; } = "camera_"; // 보관/삭제 대상 파일 접두(prefix). 기본: "camera_"
    public string[] RetainExtensions { get; set; } = new[] { "mp4", "webm", "mkv" }; // 대상 확장자 리스트. 기본: mp4, webm, mkv

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
