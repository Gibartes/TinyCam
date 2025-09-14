namespace TinyCam.Utils;

public sealed class NonceGuard
{
    public static bool Check(long ts, TimeSpan allowedSkew)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ts < now - (long)allowedSkew.TotalSeconds) return false;
        if (ts > now + (long)allowedSkew.TotalSeconds) return false;
        return true;
    }
}
