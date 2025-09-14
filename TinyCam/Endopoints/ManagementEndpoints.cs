using System.Text;
using System.Text.Json;
using TinyCam.Services;
using TinyCam.Models;
using TinyCam.Modules;
using TinyCam.Data;
using TinyCam.Services.Devices;

namespace TinyCam.Endpoints;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (HttpContext ctx) =>
        {
            return Results.Ok(new { app = "TinyCam", status = "ok" });
        });

        app.MapGet("/device", async (HttpContext ctx, IDeviceTextEnumerator dev, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey))
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
                if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey)) return Results.Unauthorized();
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
                if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey)) return Results.Unauthorized();
                await host.StopAsync();
                return Results.Ok(new { running = host.IsRunning });
            }
            catch {
                return Results.BadRequest();
            }
        });

        app.MapPost("/apply", async (HttpContext ctx, MuxerHost host, TinyCamConfig liveCfg, KeyStore ks) =>
        {
            if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey)) return Results.Unauthorized();
            try
            {
                var yaml = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync();
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
            if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey))
            { return Results.Unauthorized(); }
            try
            {
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

        app.MapPost("/update_key", async (HttpContext ctx, KeyStore ks) =>
        {
            try
            {
                if (!await Auth.VerifyHmacAsync(ctx, ks.ManagementKey)) return Results.Unauthorized();
                using var doc = JsonDocument.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync());
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
