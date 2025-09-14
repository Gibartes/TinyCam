using TinyCam.Models;

namespace TinyCam.Utils;

public static class ConfigHelpers
{
    public static void ApplyConfig(TinyCamConfig dst, TinyCamConfig src)
    {
        // 기본
        dst.Platform = src.Platform;
        dst.Device = src.Device;
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.Fps = src.Fps;
        dst.VideoCodec = src.VideoCodec;
        dst.OutputDir = src.OutputDir;
        dst.SegmentUnit = src.SegmentUnit;
        dst.SegmentSeconds = src.SegmentSeconds;
        dst.FfmpegPath = src.FfmpegPath;
        dst.Encoder = src.Encoder;

        // 안정화/입력
        dst.RtbufSize = src.RtbufSize;
        dst.ThreadQueueSize = src.ThreadQueueSize;
        dst.UseWallclockTimestamps = src.UseWallclockTimestamps;
        dst.UseLowPower = src.UseLowPower;

        // 암호화 옵션
        dst.SaveEncryptedConfig = src.SaveEncryptedConfig;
        dst.IsEncrypted = src.IsEncrypted;
        dst.Cipher = src.Cipher;

        dst.ClusterTimeLimitMs = src.ClusterTimeLimitMs;
        dst.ClusterSizeLimitBytes = src.ClusterSizeLimitBytes;

        dst.Gop = src.Gop;
        dst.KeyintMin = src.KeyintMin;
        dst.GlobalQuality = src.GlobalQuality;
        dst.BitrateKbps = src.BitrateKbps;
        dst.MaxrateKbps = src.MaxrateKbps;
        dst.BufsizeKbps = src.BufsizeKbps;

        dst.SegmentFormat = src.SegmentFormat;
        dst.PipeFormat = src.PipeFormat;
        dst.PipeLive = src.PipeLive;

        dst.EnableAudio = src.EnableAudio;
        dst.AudioDevice = src.AudioDevice;
        dst.AudioCodec  = src.AudioCodec;
        dst.AudioBitrateKbps = src.AudioBitrateKbps;
        dst.AudioSampleRate = src.AudioSampleRate;
        dst.AudioChannels = src.AudioChannels;

        dst.ExtraInputArgs = src.ExtraInputArgs;
        dst.ExtraEncoderArgs = src.ExtraEncoderArgs;
        dst.ExtraOutputArgs = src.ExtraOutputArgs;

        dst.LogMode = src.LogMode;
        dst.LogFile = src.LogFile;
        dst.LogMaxSizeMB = src.LogMaxSizeMB;
        dst.LogMaxFiles = src.LogMaxFiles;
        dst.LogRollDaily = src.LogRollDaily;

        void CopyOpt(string name)
        {
            var p = dst.GetType().GetProperty(name);
            var q = src.GetType().GetProperty(name);
            if (p != null && q != null) p.SetValue(dst, q.GetValue(src));
        }
        foreach (var n in new[] { "UseQsvFilter", "HwUploadExtraFrames", "LiveWebmTuning", "ClusterTimeLimitMs", "ClusterSizeLimitBytes", "Gop", "CaptureBackend" })
            CopyOpt(n);
    }
}
