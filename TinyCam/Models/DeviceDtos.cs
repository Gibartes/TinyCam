using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TinyCam.Models;

public sealed class DeviceInfoDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "video"; // video|audio
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("altName")] public string? AltName { get; set; } // Windows dshow Alternative name
    [JsonPropertyName("systemPath")] public string? SystemPath { get; set; } // /dev/video*
    [JsonPropertyName("formats")] public List<FormatInfoDto> Formats { get; set; } = new();
    [JsonPropertyName("inputs")] public Dictionary<string, string> Inputs { get; set; } = new();

    public string GetRecommendedDshowInput(string? provided = null)
    {
        var io = Kind == "audio" ? "audio" : "video";
        var pickAlt =
            DeviceNaming.IsWindowsAltName(provided) ||
            (!string.IsNullOrWhiteSpace(AltName) && !DeviceNaming.IsWindowsAltName(Name));

        var id = pickAlt && !string.IsNullOrWhiteSpace(AltName) ? AltName! : Name;
        return DeviceNaming.BuildDshowInput(io, id);
    }
}

public sealed class FormatInfoDto
{
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("fps")] public double? Fps { get; set; }
    [JsonPropertyName("pixelFormat")] public string? PixelFormat { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public static class DeviceNaming
{
    private static readonly Regex AltPattern =
        new(@"@device_pnp_\\\?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DshowSpec =
        new(@"^(?<io>video|audio)\s*=\s*""(?<id>.+)""$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsWindowsAltName(string? s) =>
        !string.IsNullOrWhiteSpace(s) && AltPattern.IsMatch(s);

    public static string BuildDshowInput(string io, string id) =>
        $"{(io == "audio" ? "audio" : "video")}=\"{id}\"";

    public static bool TryParseDshowInput(string s, out string io, out string id)
    {
        io = "video"; id = s;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = DshowSpec.Match(s.Trim());
        if (!m.Success) return false;
        io = m.Groups["io"].Value.ToLowerInvariant();
        id = m.Groups["id"].Value;
        return true;
    }
}

