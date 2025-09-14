using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TinyCam.Models;
using TinyCam.Services;
using TinyCam.Modules;
using TinyCam;
using TinyCam.Data;

namespace TinyCam.Endpoints;

public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/stream", async (HttpContext ctx, FFmpegMuxer mux, KeyStore ks, TinyCamConfig conf, ILoggerFactory lf) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest("websocket required");

            var token = ctx.Request.Query["token"].ToString();
            var expStr = ctx.Request.Query["exp"].ToString();
            var cnonce64 = ctx.Request.Query["cnonce"].ToString(); // 16B base64
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expStr) || string.IsNullOrWhiteSpace(cnonce64))
                return Results.Unauthorized();
            if (!long.TryParse(expStr, out var exp)) return Results.Unauthorized();

            var msg = $"stream:{exp}";
            if (!Auth.VerifyHmac(msg, token, ks.AccessKey)) return Results.Unauthorized();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return Results.Unauthorized();

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var log = lf.CreateLogger("Stream");
            using var sendCts = new CancellationTokenSource();

            var chan = Channel.CreateBounded<ArraySegment<byte>>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            var sender = Task.Run(async () =>
            {
                try
                {
                    while (await chan.Reader.WaitToReadAsync(sendCts.Token))
                    {
                        while (chan.Reader.TryRead(out var seg))
                        {
                            if (ws.State != WebSocketState.Open && ws.State != WebSocketState.CloseReceived) return;
                            await ws.SendAsync(seg, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, sendCts.Token);

            try
            {
                // Session Key(HKDF) + AAD
                byte[] clientNonce;
                try { clientNonce = Convert.FromBase64String(cnonce64); } catch { return Results.Unauthorized(); }
                if (clientNonce.Length != 16) return Results.Unauthorized();

                var session = Auth.CreateSession(ks.AccessKeyBytes, clientNonce, out var serverNonce);
                var connB64 = Convert.ToBase64String(session.ConnId); // 4B connId → b64
                var codec = conf.VideoCodec.ToString().ToLowerInvariant();
                var aad = Auth.BuildAad(connB64, exp, codec, conf.Width, conf.Height, conf.Fps);

                var hello = JsonSerializer.Serialize(new
                {
                    type = "hello",
                    snonce = Convert.ToBase64String(serverNonce),
                    conn = connB64,
                    w = conf.Width,
                    h = conf.Height,
                    fps = conf.Fps,
                    codec,
                    exp
                });

                await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, sendCts.Token);

                // Preroll (After EBML)
                bool option = !(conf.VideoCodec == Codec.h264 || conf.VideoCodec == Codec.h265);
                var init = mux.GetInitSnapshot(option);
                if (!init.IsEmpty)
                {
                    const int chunk = 64 * 1024;
                    var buf = init.ToArray();
                    for (int off = 0; off < buf.Length; off += chunk)
                    {
                        var len = Math.Min(chunk, buf.Length - off);
                        var slice = new byte[len];
                        Buffer.BlockCopy(buf, off, slice, 0, len);
                        var enc = session.Encrypt(slice, aad);
                        var arr = enc.ToArray();
                        chan.Writer.TryWrite(new ArraySegment<byte>(arr, 0, arr.Length));
                    }
                }

                // Live Subscription
                void OnChunk(ReadOnlyMemory<byte> data)
                {
                    try
                    {
                        var enc = session.Encrypt(data.Span, aad);
                        var arr = enc.ToArray();
                        chan.Writer.TryWrite(new ArraySegment<byte>(arr, 0, arr.Length));
                    }
                    catch (Exception ex) { log.LogError(ex, "stream enqueue failed"); }
                }

                var subId = mux.Subscribe(OnChunk);
                try
                {
                    var pingBuf = new byte[2];
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(pingBuf, sendCts.Token);
                        if (r.CloseStatus.HasValue) break;
                    }
                }
                finally
                {
                    mux.Unsubscribe(subId);
                    chan.Writer.TryComplete();
                    sendCts.Cancel();
                    try { await sender; } catch { /* ignore */ }

                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                    }
                }
            }
            catch
            {
                try
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "error", CancellationToken.None);
                }
                catch { }
            }

            return Results.Empty;
        });
    }
}
