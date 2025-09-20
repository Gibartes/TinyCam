using System.Text.Json;

namespace TinyCam.Data;

public class KeyStore
{
    private readonly string _path;
    public string ManagementKey { get; private set; }
    public string AccessKey { get; private set; }
    public byte[] AccessKeyBytes => Convert.FromBase64String(AccessKey);

    public KeyStore(string path)
    {
        _path = path;
        if (File.Exists(_path))
        {
            var json = JsonDocument.Parse(File.ReadAllText(_path));
            ManagementKey = json.RootElement.GetProperty("managementKey").GetString() ?? "";
            AccessKey = json.RootElement.GetProperty("accessKey").GetString() ?? "";
        }
        else
        {
            ManagementKey = GenerateRandomKey(32);
            AccessKey = GenerateRandomKey(32);
            Save();
        }
    }

    public void RotateAccessKey(string newKeyB64)
    {
        if (string.IsNullOrWhiteSpace(newKeyB64)) throw new ArgumentException("empty key");
        if(newKeyB64.Length < 16) throw new ArgumentException("weak key");
        AccessKey = newKeyB64;
        Save();
    }

    public void RotateManagementKey(string newKeyB64)
    {
        if (string.IsNullOrWhiteSpace(newKeyB64)) throw new ArgumentException("empty key");
        if (newKeyB64.Length < 16) throw new ArgumentException("weak key");
        ManagementKey = newKeyB64;
        Save();
    }

    public string GenerateRandomKey(int length)
    {
        if (length < 16) return "";
        return Convert.ToBase64String(RandomBytes(length));
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(new { managementKey = ManagementKey, accessKey = AccessKey }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }
}
