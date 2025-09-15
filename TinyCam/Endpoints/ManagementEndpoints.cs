using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using TinyCam.Data;
using TinyCam.Models;
using TinyCam.Modules;
using TinyCam.Services;
using TinyCam.Services.Devices;
using TinyCam.Utils;

namespace TinyCam.Endpoints;

public static class ManagementEndpoints
{
    public static bool TimeCheck(JsonDocument? document)
    {
        if (document == null) { return false; }
        if (!document.RootElement.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number) return false;
        return NonceGuard.Check(tsEl.GetInt64(), TimeSpan.FromSeconds(600));
    }

    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Ok(new { app = "TinyCam", status = "ok" });
        });

        app.MapGet("/device", async (HttpContext ctx, IDeviceTextEnumerator dev, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
            if (!result.Item1)
                return Results.Unauthorized();

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
                var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!result.Item1 || result.Item2 == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(result.Item2);
                if(!TimeCheck(doc)) return Results.Unauthorized();
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
                var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!result.Item1 || result.Item2 == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(result.Item2);
                if (!TimeCheck(doc)) return Results.Unauthorized();
                await host.StopAsync();
                return Results.Ok(new { running = host.IsRunning });
            }
            catch {
                return Results.BadRequest();
            }
        });

        app.MapPost("/apply", async (HttpContext ctx, MuxerHost host, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!result.Item1 || result.Item2 == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(result.Item2);
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

        app.MapPost("/apply-config", async (HttpContext ctx, MuxerHost host, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            try
            {
                var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!result.Item1 || result.Item2 == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(result.Item2);
                if (!TimeCheck(doc)) return Results.Unauthorized();
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
                var result = await Auth.VerifyHmacAsync(ctx, ks.ManagementKey);
                if (!result.Item1 || result.Item2 == null)
                    return Results.Unauthorized();
                using var doc = JsonDocument.Parse(result.Item2);
                if (!TimeCheck(doc)) return Results.Unauthorized();
                if (!doc.RootElement.TryGetProperty("accessKey", out var ak)) return Results.BadRequest(new { error = "missing accessKey" });
                ks.RotateAccessKey(ak.GetString() ?? "");
                return Results.Ok(new { rotated = true });
            }
            catch
            {
                return Results.Forbid();
            }
        });
    }
}
