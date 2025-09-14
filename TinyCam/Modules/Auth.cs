using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace TinyCam.Modules;

public static class Auth
{
    // ---------------- HMAC (Authenticate Management Token) ----------------
    private static bool TryB64OrB64UrlDecode(string s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace(' ', '+');

        try
        {
            bytes = WebEncoders.Base64UrlDecode(s);
            return true;
        }
        catch { /* try normal base64 next */ }

        try
        {
            var norm = s.Replace('-', '+').Replace('_', '/');
            switch (norm.Length % 4)
            {
                case 2: norm += "=="; break;
                case 3: norm += "="; break;
            }
            bytes = Convert.FromBase64String(norm);
            return true;
        }
        catch { return false; }
    }

    public static bool VerifyHmac(string message, string signatureB64OrUrl, string keyB64OrUrl)
    {
        if (!TryB64OrB64UrlDecode(signatureB64OrUrl, out var sig)) return false;
        if (!TryB64OrB64UrlDecode(keyB64OrUrl, out var key)) return false;

        using var h = new HMACSHA256(key);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(message));
        return CryptographicOperations.FixedTimeEquals(mac, sig);
    }

    public static string SignHmacUrlSafe(string message, string keyB64OrUrl)
    {
        if (!TryB64OrB64UrlDecode(keyB64OrUrl, out var key))
            throw new ArgumentException("invalid key base64", nameof(keyB64OrUrl));
        using var h = new HMACSHA256(key);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(message));
        return WebEncoders.Base64UrlEncode(mac);
    }

    public static async Task<bool> VerifyHmacAsync(HttpContext ctx, string keyB64)
    {
        try
        {
            var sig = ctx.Request.Headers["X-TinyCam-Auth"].ToString();
            if (string.IsNullOrEmpty(sig)) return false;
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await sr.ReadToEndAsync();
            return VerifyHmac(body, sig, keyB64);
        }
        catch{
            return false;
        }
    }

    // ---------------- HKDF-SHA256 (Generate Session Key) ----------------
    public static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        // Extract
        byte[] prk;
        using (var hmac = new HMACSHA256(salt))
            prk = hmac.ComputeHash(ikm);

        // Expand
        var okm = new byte[length];
        var t = Array.Empty<byte>();
        var pos = 0;
        var blockIndex = 1;
        while (pos < length)
        {
            using var hmac = new HMACSHA256(prk);
            hmac.TransformBlock(t, 0, t.Length, null, 0);
            hmac.TransformBlock(info, 0, info.Length, null, 0);
            var ctr = new[] { (byte)blockIndex };
            hmac.TransformFinalBlock(ctr, 0, ctr.Length);
            t = hmac.Hash!;
            var toCopy = Math.Min(t.Length, length - pos);
            Buffer.BlockCopy(t, 0, okm, pos, toCopy);
            pos += toCopy;
            blockIndex++;
        }
        CryptographicOperations.ZeroMemory(prk);
        return okm;
    }

    // ---------------- Encryted Session Context ----------------
    public sealed class SessionCrypto
    {
        private readonly byte[] _key;      // 32B session key
        private readonly byte[] _connId;   // 4B
        private ulong _counter;            // 8B big-endian

        public SessionCrypto(byte[] key, byte[] connId)
        {
            _key = key; _connId = connId;
            _counter = 0UL;
        }

        public byte[] ConnId => _connId;

        private byte[] NextNonce()
        {
            var nonce = new byte[12];
            Buffer.BlockCopy(_connId, 0, nonce, 0, 4);
            var next = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(++_counter);
            var buf = BitConverter.GetBytes(next);
            Buffer.BlockCopy(buf, 0, nonce, 4, 8);
            return nonce;
        }

        /// AES-GCM(Tag 16B). Frame: [nonce(12)][tag(16)][ciphertext]
        public ReadOnlyMemory<byte> Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad)
        {
            var nonce = NextNonce();
            var tag = new byte[16];
            var ct = new byte[plain.Length];
            using var g = new AesGcm(_key, 16);
            g.Encrypt(nonce, plain, ct, tag, aad);

            var outBuf = new byte[12 + 16 + ct.Length];
            Buffer.BlockCopy(nonce, 0, outBuf, 0, 12);
            Buffer.BlockCopy(tag, 0, outBuf, 12, 16);
            Buffer.BlockCopy(ct, 0, outBuf, 28, ct.Length);
            return outBuf;
        }
    }

    /// Session Handshake: (PSK=accessKeyBytes, clientNonce(16B), serverNonce(16B)) ¡æ sessionKey(32B)
    public static SessionCrypto CreateSession(byte[] accessKeyBytes, byte[] clientNonce, out byte[] serverNonce)
    {
        serverNonce = RandomNumberGenerator.GetBytes(16);
        var salt = new byte[32];
        Buffer.BlockCopy(clientNonce, 0, salt, 0, 16);
        Buffer.BlockCopy(serverNonce, 0, salt, 16, 16);
        var info = Encoding.UTF8.GetBytes("tinycam hkdf v1");
        var sessionKey = HkdfSha256(accessKeyBytes, salt, info, 32);

        var connId = RandomNumberGenerator.GetBytes(4); // 32-bit Idenfitier
        return new SessionCrypto(sessionKey, connId);
    }

    /// Stream parameter: AAD
    public static byte[] BuildAad(string streamId, long exp, string codec, int w, int h, int fps)
    {
        var s = $"{streamId}|{exp}|{codec}|{w}x{h}|{fps}";
        return Encoding.UTF8.GetBytes(s);
    }
}