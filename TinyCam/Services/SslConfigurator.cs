
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using TinyCam.Models; // TinyCamConfig

namespace TinyCam.Services;


/* 
    * TinyCam Kestrel (HTTP/HTTPS/WSS) endpoint configuration helper.
    * - Enables HTTPS/WSS using two PEM files (certificate/key)
    * - Supports simultaneous HTTP binding and optional HTTP→HTTPS redirection
    * - Supports toggling HTTP/2 and HTTP/3
*/

public static class SslConfigurator
{
    public static void ConfigureFromConfiguration(
        IWebHostBuilder webHost,
        IConfiguration configuration,
        TinyCamConfig cfg,
        ILogger? log = null)
    {
        webHost.ConfigureKestrel((ctx, options) =>
        {
            var endpoints = configuration.GetSection("Kestrel:Endpoints");
            var children = endpoints.Exists() ? endpoints.GetChildren().ToList() : null;

            if (children == null || children.Count == 0)
            {
                return;
            }

            X509Certificate2? cert = null;
            if (children.Any(c =>
            {
                var url = c.GetValue<string>("Url");
                return Uri.TryCreate(url, UriKind.Absolute, out var u)
                        && string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            }))
            {
                try
                {
                    cert = LoadServerCertificate(cfg, log);
                }
                catch (Exception ex)
                {
                    log?.LogError(ex, "Failed to load TLS certificate. HTTPS endpoints will start without certificate.");
                }
            }

            foreach (var ep in children)
            {
                var url = ep.GetValue<string>("Url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                int port = uri.Port > 0 ? uri.Port : 0;
                var isHttps = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

                void ConfigureListen(ListenOptions lo)
                {
                    lo.Protocols = HttpProtocols.Http1AndHttp2;
                    if (isHttps && cert != null)
                    {
                        lo.UseHttps(https =>
                        {
                            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            https.ServerCertificate = cert;
                        });
                    }
                }

                var host = (uri.Host ?? "").Trim();
                var bindAll = host == "0.0.0.0" || host == "::" || host == "*" || host == "+";

                if (bindAll)
                    options.ListenAnyIP(port, ConfigureListen);
                else if (IPAddress.TryParse(host, out var ip))
                    options.Listen(ip, port, ConfigureListen);
                else
                    options.ListenLocalhost(port, ConfigureListen);

                log?.LogInformation("Kestrel bound {scheme}://{host}:{port}", uri.Scheme, host, port);
            }
        });
    }

    // ── Certificate loading (PFX/PEM) ─────────────────────────────────

    private static X509Certificate2 LoadServerCertificate(TinyCamConfig cfg, ILogger? log)
    {
        var mode = (cfg.CertificateType ?? "pfx").Trim().ToLowerInvariant();
        return mode switch
        {
            "pfx" => LoadPfx(cfg, log),
            "pem" => LoadPem(cfg, log),
            _ => throw new InvalidOperationException($"Unknown CertificateType: {cfg.CertificateType}")
        };
    }

    private static X509Certificate2 LoadPfx(TinyCamConfig cfg, ILogger? log)
    {
        var pfxPath = cfg.PfxPath ?? throw new InvalidOperationException("PFX mode requires PfxPath.");
        var pfxPwd = cfg.Password;

        if (!File.Exists(pfxPath))
            throw new FileNotFoundException("PFX file not found.", pfxPath);

        log?.LogInformation("Loading PFX certificate: {path}", pfxPath);
        return new X509Certificate2(pfxPath, pfxPwd, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static X509Certificate2 LoadPem(TinyCamConfig cfg, ILogger? log)
    {
        var certPath = cfg.PemCertPath ?? throw new InvalidOperationException("PEM mode requires PemCertPath.");
        var keyPath = cfg.PemKeyPath ?? throw new InvalidOperationException("PEM mode requires PemKeyPath.");
        var keyPwd = cfg.Password;

        if (!File.Exists(certPath)) throw new FileNotFoundException("PEM cert file not found.", certPath);
        if (!File.Exists(keyPath)) throw new FileNotFoundException("PEM key file not found.", keyPath);

        log?.LogInformation("Loading PEM certificate: cert={cert}, key={key}", certPath, keyPath);

#if NET8_0_OR_GREATER
        X509Certificate2 cert =
            !string.IsNullOrEmpty(keyPwd)
                ? X509Certificate2.CreateFromEncryptedPemFile(certPath, keyPath, keyPwd)
                : X509Certificate2.CreateFromPemFile(certPath, keyPath);

        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
#else
        if (!string.IsNullOrEmpty(keyPwd))
            throw new NotSupportedException("Encrypted PEM requires .NET 8 or greater.");

        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
#endif
    }
}

