using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TinyCam.Models;
using TinyCam.Services;
using TinyCam.Modules;
using TinyCam.Data;

namespace TinyCam.Endpoints;

public static class StreamEndpoints
{

    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/stream", async (HttpContext ctx, FFmpegMuxer mux, KeyStore ks, TinyCamConfig conf, ILoggerFactory lf, IHostApplicationLifetime life) =>
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

            var log = lf.CreateLogger("Stream");
            Task? sender = null;
            int? subId = null;
            var channelCapacity = (conf.ChannelCapacity > 1 && conf.ChannelCapacity < 4097) ? conf.ChannelCapacity : 256;
            var inactivityTimeout = (conf.ChannelTimeout > 1 && conf.ChannelTimeout < 3601) ? TimeSpan.FromSeconds(conf.ChannelTimeout) : TimeSpan.FromSeconds(60);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

            // 단일 리더/다중 라이터(프리롤 + OnChunk) 채널
            var chan = Channel.CreateBounded<ArraySegment<byte>>(new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

 
            // ── Session Key(HKDF) + AAD ────────────────────────────────────────────
            byte[] clientNonce;
            try { clientNonce = Convert.FromBase64String(cnonce64); } catch { return Results.Unauthorized(); }
            if (clientNonce.Length != 16) return Results.Unauthorized();

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            var shutdownReg = life.ApplicationStopping.Register(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            try { await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server shutting down", t.Token); }
                            catch { /* ignore */ }
                        }
                    }
                    finally
                    {
                        try { ws.Abort(); } catch { }
                    }
                });
            });

            try
            {
                var session = Auth.CreateSession(ks.AccessKeyBytes, clientNonce, out var serverNonce);
                var connB64 = Convert.ToBase64String(session.ConnId); // 4B connId → b64
                var codec = conf.VideoCodec.ToString().ToLowerInvariant();
                var aad = Auth.BuildAad(connB64, exp, codec, conf.Width, conf.Height, conf.Fps);

                // ── Server → Client: hello ────────────────────────────────────────────
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

                // ── Client → Server: start/request/ready  ───────
                var started = await WaitForClientStartAsync(ws, connB64, exp, log, inactivityTimeout, sendCts.Token);
                if (!started)
                {
                    await SafeCloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "no start signal", log);
                    return Results.Empty;
                }

                // ── Sender Task ────────────────────────────────
                sender = Task.Run(async () =>
                {
                    try
                    {
                        while (await chan.Reader.WaitToReadAsync(sendCts.Token))
                        {
                            while (chan.Reader.TryRead(out var seg))
                            {
                                var state = ws.State;
                                if (state != WebSocketState.Open && state != WebSocketState.CloseReceived) return;
                                await ws.SendAsync(seg, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                }, sendCts.Token);

                // ── Preroll (After EBML) ───────────────────────────────────────
                bool option = !(conf.VideoCodec == Codec.h264 || conf.VideoCodec == Codec.h265);
                var init = mux.GetInitSnapshot(option); // ReadOnlyMemory<byte>
                if (!init.IsEmpty)
                {
                    const int CHUNK = 64 * 1024;
                    ReadOnlyMemory<byte> mem = init;
                    for (int off = 0; off < mem.Length; off += CHUNK)
                    {
                        int len = Math.Min(CHUNK, mem.Length - off);
                        var slice = mem.Slice(off, len);
                        var enc = session.Encrypt(slice.Span, aad);
                        var arr = enc.ToArray();
                        chan.Writer.TryWrite(new ArraySegment<byte>(arr));
                    }
                }

                // ── Live Subscription ────────────────────────────────────────────────
                void OnChunk(ReadOnlyMemory<byte> data)
                {
                    try
                    {
                        if (sendCts.IsCancellationRequested) return;
                        var enc = session.Encrypt(data.Span, aad);
                        var arr = enc.ToArray();
                        chan.Writer.TryWrite(new ArraySegment<byte>(arr, 0, arr.Length));
                    }
                    catch (Exception ex) { log.LogError(ex, "stream enqueue failed"); }
                }

                subId = mux.Subscribe(OnChunk);

                var end = await RunReceiveLoopAsync(ws, inactivityTimeout, sendCts.Token);
                log.LogInformation("stream ended: {Reason}", end.ToString());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "stream error");
                try
                {
                    if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "error", CancellationToken.None);
                }
                catch { }
            }
            finally
            {
                if (subId is int id) { try { mux.Unsubscribe(id); } catch { } }
                try { chan.Writer.TryComplete(); } catch { }
                try { sendCts.Cancel(); } catch { }
                try { if (sender is not null) await sender; } catch { }
                try { shutdownReg.Dispose(); } catch { }
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                }
            }

            return Results.Empty;
        });
    }

    // ─────────────────────────────── Helpers ───────────────────────────────

    private static async Task<bool> WaitForClientStartAsync(
        WebSocket ws, string conn, long exp, ILogger log, TimeSpan timeout, CancellationToken ct)
    {
        var json = await ReceiveTextOnceWithTimeoutAsync(ws, timeout, ct);
        if (json is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var typ = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            if (typ is not ("start" or "request" or "ready"))
            {
                log.LogWarning("unexpected start type: {Type}", typ);
                return false;
            }

            if (root.TryGetProperty("conn", out var cEl))
            {
                var c = cEl.GetString();
                if (!string.Equals(c, conn, StringComparison.Ordinal))
                {
                    log.LogWarning("conn mismatch (got:{Got}, expect:{Exp})", c, conn);
                    return false;
                }
            }

            if (root.TryGetProperty("exp", out var eEl) &&
                eEl.ValueKind == JsonValueKind.Number &&
                eEl.TryGetInt64(out var eVal) &&
                eVal != exp)
            {
                log.LogWarning("exp mismatch (got:{Got}, expect:{Exp})", eVal, exp);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "invalid start json");
            return false;
        }
    }

    private static async Task<string?> ReceiveTextOnceWithTimeoutAsync(WebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(4096);
        try
        {
            var sb = new StringBuilder();

            while (true)
            {
                var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                var timeoutTask = Task.Delay(timeout, ct);
                var completed = await Task.WhenAny(receiveTask, timeoutTask);

                if (completed == timeoutTask) return null;
                var r = await receiveTask;

                if (r.CloseStatus.HasValue) return null;

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    while (!r.EndOfMessage)
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (r.CloseStatus.HasValue) return null;
                    }
                    continue;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (r.EndOfMessage) break;
            }

            return sb.ToString();
        }
        finally { pool.Return(buffer); }
    }

    private enum ReceiveEndReason { Closed, Inactivity, Error, Canceled }

    private static async Task<ReceiveEndReason> RunReceiveLoopAsync(
        WebSocket ws, TimeSpan inactivityTimeout, CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(2048);

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                var delayTask = Task.Delay(inactivityTimeout, ct);
                var completed = await Task.WhenAny(receiveTask, delayTask);

                if (completed == delayTask) return ReceiveEndReason.Inactivity;

                WebSocketReceiveResult r;
                try { r = await receiveTask; }
                catch (OperationCanceledException) { return ReceiveEndReason.Canceled; }
                catch (WebSocketException) { return ReceiveEndReason.Error; }

                if (r.CloseStatus.HasValue) return ReceiveEndReason.Closed;

                while (!r.EndOfMessage)
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (r.CloseStatus.HasValue) return ReceiveEndReason.Closed;
                }
            }

            return ReceiveEndReason.Closed;
        }
        catch (OperationCanceledException)
        {
            return ReceiveEndReason.Canceled;
        }
        catch (WebSocketException)
        {
            return ReceiveEndReason.Error;
        }
        finally
        {
            pool.Return(buffer);
        }
    }
    private static async Task SafeCloseAsync(WebSocket ws, WebSocketCloseStatus code, string desc, ILogger log)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(code, desc, CancellationToken.None);
        }
        catch (Exception ex) { log.LogDebug(ex, "safe close error"); }
    }
}
