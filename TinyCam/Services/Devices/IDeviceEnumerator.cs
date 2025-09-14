using TinyCam.Models;

namespace TinyCam.Services.Devices;

public interface IDeviceTextEnumerator
{
    Task<string> ListAsync(string kind, bool includeFormats, bool includeAlternativeName, CancellationToken ct = default);
    Task<string> GetAsync(string identifier, string? kind, bool includeAlternativeName, CancellationToken ct = default);
}
