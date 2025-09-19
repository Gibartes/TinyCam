using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using SQLitePCL;
using System.IO;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using TinyCam.Data;
using TinyCam.Models;
using TinyCam.Modules;
using TinyCam.Services;
using TinyCam.Services.Devices;
using TinyCam.Utils;
using static TinyCam.Models.RequestDTO;

namespace TinyCam.Endpoints;

public static class ManagementEndpoints
{
    public static JsonSerializerOptions jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TimeCheck(JsonDocument? document)
    {
        if (document == null) { return false; }
        if (!document.RootElement.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number) return false;
        return NonceGuard.Check(tsEl.GetInt64(), TimeSpan.FromSeconds(120));
    }

    public static bool TimeCheck(long document)
    {
        return NonceGuard.Check(document, TimeSpan.FromSeconds(120));
    }

    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Ok(new { status = "ok" });
        });

        app.MapPost("/device", async (HttpContext ctx, IDeviceTextEnumerator dev, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
            if (!ok || body == null)
                return Results.Unauthorized();
            using var doc = JsonDocument.Parse(body);

            string kind = (ctx.Request.Query["kind"].ToString() ?? "all").ToLowerInvariant(); // video|audio|all
            string name = ctx.Request.Query["name"].ToString() ?? "";
            bool expand = ctx.Request.Query["expand"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            string text = string.IsNullOrWhiteSpace(name)
                ? await dev.ListAsync(kind, includeFormats: expand, includeAlternativeName: true, ctx.RequestAborted)
                : await dev.GetAsync(name, kind, includeAlternativeName: true, ctx.RequestAborted);
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapPost("/start", async (HttpContext ctx, MuxerHost host, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!ok || body == null)
                    return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<GeneralPostRequest>(body ?? "", jsonOpts);
                if (req is null || !TimeCheck(req.ts)) return Results.Unauthorized();
                await host.StartAsync();
                return Results.Ok(new { running = host.IsRunning });
            }
            catch
            {
                return Results.BadRequest();
            }
        });

        app.MapPost("/stop", async (HttpContext ctx, MuxerHost host, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!ok || body == null)
                    return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<GeneralPostRequest>(body ?? "", jsonOpts);
                if (req is null || !TimeCheck(req.ts)) return Results.Unauthorized();
                await host.StopAsync();
                return Results.Ok(new { running = host.IsRunning });
            }
            catch {
                return Results.BadRequest();
            }
        });

        /* disable direct modification of configuration considering security options
        app.MapPost("/apply", async (HttpContext ctx, MuxerHost host, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!ok || body == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(body);
                if (!TimeCheck(doc)) return Results.Unauthorized();
                if (!doc.RootElement.TryGetProperty("config", out var tsEl))
                {
                    return Results.UnprocessableEntity();
                }
                var yaml   = tsEl.ToString();
                var parsed = TinyCamConfig.ParseAndMaybeEncrypt(yaml, ks);
                TinyCamConfig.Save("config.yaml", parsed, ks);
                Utils.ConfigHelpers.ApplyConfig(liveCfg, parsed);
                await host.RestartAsync();
                return Results.Ok(new { applied = true, running = host.IsRunning });
            }
            catch
            {
                return Results.UnprocessableEntity();
            }
        });
        */

        app.MapPost("/apply-config", async (HttpContext ctx, MuxerHost host, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!ok || body == null)
                    return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<GeneralPostRequest>(body ?? "", jsonOpts);
                if (req is null || !TimeCheck(req.ts)) return Results.Unauthorized();
                var configPath = Environment.GetEnvironmentVariable("TINY_CAM_CONFIG") ?? "config.yaml";
                var newCfg = TinyCamConfig.Load(configPath, ks);
                Utils.ConfigHelpers.ApplyConfig(liveCfg, newCfg);
                await host.RestartAsync();
                return Results.Ok(new { applied = true, running = host.IsRunning });
            }
            catch
            {
                return Results.UnprocessableEntity();
            }
        });

        app.MapPost("/update-key", async (HttpContext ctx, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!ok || body == null)
                    return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<UpgradeKeyRequest>(body ?? "", jsonOpts);
                if (req is null || !TimeCheck(req.ts)) return Results.Unauthorized();
                
                ks.RotateAccessKey(req.accessKey ?? "");
                return Results.Ok(new { rotated = true });
            }
            catch
            {
                return Results.BadRequest();
            }
        });

        app.MapPost("/file/list", async (HttpContext ctx, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacUserAsync(ctx, liveCfg, ks);
                if (!ok || body == null) return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<GeneralPostRequest>(body ?? "", jsonOpts);
                if (req is null || !TimeCheck(req.ts)) return Results.Unauthorized();
                if (!liveCfg.EnableDownload) return Results.StatusCode(403);
                if (!Directory.Exists(liveCfg.OutputDir)) return Results.StatusCode(403);
                var extSet = liveCfg.RetainExtensions.Select(e => e.StartsWith(".") ? e.ToLower() : "." + e.ToLower()).ToHashSet();
                var fileList = Directory.EnumerateFiles(liveCfg.OutputDir, "*", SearchOption.TopDirectoryOnly).Where(f => extSet.Contains(Path.GetExtension(f).ToLower())).Select(Path.GetFileName).ToArray();
                var response = new
                {  
                    data = fileList,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                return Results.Ok(response);
            }
            catch
            {
                return Results.BadRequest();
            }
        });

        app.MapPost("/file/download", async (HttpContext ctx, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var (ok, body) = await Auth.VerifyHmacUserAsync(ctx, liveCfg, ks);
                if (!ok || body == null) return Results.Unauthorized();
                var req = JsonSerializer.Deserialize<DownloadRequest>(body ?? "", jsonOpts);
                if (req is null || string.IsNullOrWhiteSpace(req.name)) return Results.BadRequest();
                if (!TimeCheck(req.ts)) return Results.Unauthorized();
                if (!liveCfg.EnableDownload) return Results.StatusCode(403);
                if (!Directory.Exists(liveCfg.OutputDir)) return Results.StatusCode(403);

                var fileNameOnly = Path.GetFileName(req.name);
                if (!string.Equals(req.name, fileNameOnly, StringComparison.Ordinal)) return Results.BadRequest();

                var extSet = liveCfg.RetainExtensions.Select(e => e.StartsWith(".") ? e.ToLower() : "." + e.ToLower()).ToHashSet();
                var ext = Path.GetExtension(fileNameOnly).ToLowerInvariant();
                if (!extSet.Contains(ext)) return Results.StatusCode(403);
                
                var root = Path.GetFullPath(liveCfg.OutputDir);
                var fullPath = Path.GetFullPath(Path.Combine(root, fileNameOnly));
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(403);

                if (!File.Exists(fullPath)) return Results.NotFound();
                
                var fi = new FileInfo(fullPath);
                var lastMod = fi.LastWriteTimeUtc;
                var etag = $"\"{fi.Length:x}-{lastMod.Ticks:x}\"";

                if (ctx.Request.Headers.TryGetValue("If-None-Match", out var inm) && inm.ToString() == etag)
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                if (DateTimeOffset.TryParse(ctx.Request.Headers["If-Modified-Since"], out var ims)
                    && ims.UtcDateTime >= lastMod)
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fileNameOnly, out var contentType))
                    contentType = "application/octet-stream";

                var stream = new FileStream(
                    fullPath,
                    new FileStreamOptions
                    {
                        Access = FileAccess.Read,
                        Mode = FileMode.Open,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });

                var asAttachment = req.attachment.GetValueOrDefault(true);
                var disp = asAttachment ? "attachment" : "inline";
                var utf8Name = Uri.EscapeDataString(fileNameOnly);
                ctx.Response.Headers[HeaderNames.ContentDisposition] = $"{disp}; filename=\"{fileNameOnly}\"; filename*=UTF-8''{utf8Name}";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
                ctx.Response.Headers[HeaderNames.CacheControl] = "private, max-age=0, must-revalidate";

                return Results.File(
                    fileStream: stream,
                    contentType: contentType,
                    fileDownloadName: asAttachment ? fileNameOnly : null,
                    enableRangeProcessing: true,
                    lastModified: lastMod,
                    entityTag: new EntityTagHeaderValue(etag)
                );
            }
            catch
            {
                return Results.BadRequest();
            }
        });

    }
}
