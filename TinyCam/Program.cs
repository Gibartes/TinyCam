using TinyCam.Data;
using TinyCam.Endpoints;
using TinyCam.Logging;
using TinyCam.Models;
using TinyCam.Platform;
using TinyCam.Services;
using TinyCam.Services.Devices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);

var rootPath = "";
if (Environment.GetEnvironmentVariable("TINY_CAM_CONFIG") != null)
{
    rootPath = Environment.GetEnvironmentVariable("TINY_CAM_CONFIG");
}
else {
    rootPath = Path.GetDirectoryName(Environment.ProcessPath!);
}

var configPath = Path.Combine(rootPath ?? "", @"config.yaml");
var keyPath    = Path.Combine(rootPath ?? "", @"keys.json");

var ks         = new KeyStore(keyPath);
var cfg        = TinyCamConfig.Load(configPath, ks);

builder.Logging.ClearProviders();

if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService(o => o.ServiceName = "TinyCam");
    builder.Host.UseContentRoot(AppContext.BaseDirectory);
}

SslConfigurator.ConfigureFromConfiguration(builder.WebHost, builder.Configuration, cfg);

switch ((cfg.LogMode ?? "stdout").ToLowerInvariant())
{
    case "none":
        break;

    case "fileoutput":
        var maxBytes = (long)Math.Max(1, cfg.LogMaxSizeMB) * 1024L * 1024L;
        builder.Logging.AddProvider(
            new RotatingFileLoggerProvider(cfg.LogFile, maxBytes, cfg.LogMaxFiles, cfg.LogRollDaily)
        );
        break;

    case "stdout":
    default:
        builder.Logging.AddSimpleConsole(o =>
        {
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
            o.SingleLine = true;
            o.IncludeScopes = false;
        });
        break;
}

if (OperatingSystem.IsWindows()) {
    builder.Services.AddSingleton<IProcessGuardian, WindowsProcessGuardian>();
    builder.Services.AddSingleton<IDeviceTextEnumerator, WinDshowTextEnumerator>();
}
else
{
    builder.Services.AddSingleton<IProcessGuardian>(sp =>
        new UnixProcessGuardian().WithConfig(sp.GetRequiredService<TinyCamConfig>()));
    builder.Services.AddSingleton<IDeviceTextEnumerator, UnixV4l2TextEnumerator>();
}


builder.Services.AddSingleton(new KeyStore(keyPath));
builder.Services.AddSingleton<TinyCamConfig>(sp =>
{
    var ks = sp.GetRequiredService<KeyStore>();
    return TinyCamConfig.Load(configPath, ks);
});

builder.Services.AddSingleton<FFmpegMuxer>();
builder.Services.AddSingleton<MuxerHost>();

builder.Services.AddHostedService<CaptureMutexGuard>();
builder.Services.AddHostedService<MuxerAutostart>();
builder.Services.AddHostedService<FileRetentionService>();

builder.Services.AddRouting();
builder.Services.AddLogging();


var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapManagementEndpoints();
app.MapStreamEndpoints();
app.Run();