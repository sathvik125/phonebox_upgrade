using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The Now playing widget's full-screen video: the window of the app that plays the current media session, captured and
/// encoded to H.264 live while the phone shows it, and nothing else of the screen. Off unless a Now playing widget on an
/// enabled page has "Show the video when tapped" on. The sound stays on this computer.
///
/// The answer to GET /api/screen is a stream of messages, each a 4-byte big-endian length (of what follows), a type byte
/// and the payload: 1 = setup (UTF-8 JSON: codec, width, height), 2 = key frame, 3 = other frame; a frame's payload is
/// an 8-byte big-endian timestamp in microseconds and one Annex B access unit. ?w= and ?h= give the phone's screen in
/// pixels, so a tablet gets a sharp picture and a small phone no more than it shows. One phone at a time: a new request ends
/// the stream before it. Before streaming, a refusal is JSON { state }: "off", "nothing" (nothing playing),
/// "no-window" (the player's window isn't found), "unsupported".
/// </summary>
public sealed class ScreenFeed(ConfigStore config, IServiceProvider services, ILogger<ScreenFeed> log)
{
    /// <summary>
    /// Without a size in the request (an older app): at most 960 px wide. With one (the phone's screen, or the size the
    /// viewer's settings ask for), the picture fits it, up to <see cref="LargestW"/> x <see cref="LargestH"/>, and never
    /// more than the window itself.
    /// </summary>
    public const int MaxWidth = 960, LargestW = 3840, LargestH = 2160, Fps = 30;
    internal const byte Setup = 1, KeyFrame = 2, Frame = 3;

    public static bool Allowed(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "media" && w.Bool("video") == true);

#if WINDOWS
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
#endif

    public async Task Stream(HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers.CacheControl = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        if (!Allowed(config.Get())) { await Refuse(res, 403, "off"); return; }
#if WINDOWS
        if (!WindowCapture.Supported) { await Refuse(res, 501, "unsupported"); return; }
        var media = services.GetService<MediaMonitor>();
        var app = media?.SourceAppId;
        if (media?.Snapshot() is not { } snap || string.IsNullOrEmpty(app)) { await Refuse(res, 404, "nothing"); return; }
        var hwnd = PlayerWindow.Find(app, snap.Title);
        if (hwnd == 0) { await Refuse(res, 404, "no-window"); return; }

        // one viewer: the stream before this one ends
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        lock (_gate) { _current?.Cancel(); _current = mine; }
        var ct = mine.Token;
        try
        {
            res.ContentType = "application/octet-stream";
            await res.StartAsync(ct);
            await Pump(hwnd, res.Body, ScreenSize(ctx.Request), Quality(ctx.Request), ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the phone went away
        catch (Exception ex) { log.LogWarning("screen: {Msg}", ex.Message); }
        finally
        {
            lock (_gate) { if (_current == mine) _current = null; }
            mine.Dispose();
        }
#else
        _ = services; _ = log; // the capture and the encoder are Windows only
        await Refuse(res, 501, "unsupported");
#endif
    }

    /// <summary>The largest picture the phone asked for (its screen), or 960 wide for an app that doesn't say.</summary>
    internal static (int W, int H) ScreenSize(HttpRequest req)
    {
        int.TryParse(req.Query["w"], out var w);
        int.TryParse(req.Query["h"], out var h);
        if (w < 160 || h < 90) return (MaxWidth, MaxWidth); // no size: the old limit, width only
        // either way round: a phone held upright still shows a wide video across its long side
        var (lo, hi) = (Math.Min(w, h), Math.Max(w, h));
        return (Math.Min(hi, LargestW), Math.Min(lo, LargestH));
    }

    /// <summary>The viewer's quality setting: 1 standard, 2 high, 3 max (that many times the standard bitrate).</summary>
    internal static int Quality(HttpRequest req) => int.TryParse(req.Query["q"], out var q) ? Math.Clamp(q, 1, 3) : 1;

    /// <summary>
    /// About 0.12 bits a pixel at 30 frames a second, times the quality: standard is 3 Mbps at 960 x 540 and 7.5 Mbps at
    /// 1920 x 1080; max is three times that, at most 40 Mbps.
    /// </summary>
    internal static int BitrateFor(int w, int h, int quality = 1) =>
        (int)Math.Clamp(w * (long)h * Fps * 0.12 * quality, 1_500_000, 40_000_000);

    private static Task Refuse(HttpResponse res, int status, string state)
    {
        res.StatusCode = status;
        return res.WriteAsJsonAsync(new { state });
    }

#if WINDOWS
    private async Task Pump(nint hwnd, Stream body, (int W, int H) screen, int quality, CancellationToken ct)
    {
        using var capture = WindowCapture.Start(hwnd);
        log.LogInformation("screen: streaming a window to the phone");
        H264Encoder? encoder = null;
        byte[]? nv12 = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastFrameMs = -1000, lastKeyMs = 0;
        var announced = false;
        var keyNext = true;
        var frameMs = 1000 / Fps;
        (double X, double Y, double W, double H)? area = null;
        long areaAt = -1000;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await capture.Next(TimeSpan.FromMilliseconds(1000), ct);
                var now = clock.ElapsedMilliseconds;
                if (frame is not null)
                {
                    try
                    {
                        // a browser: only the page, without its tabs and toolbar; looked up again twice a second, so
                        // theater mode, full screen or a resized window follow
                        if (now - areaAt >= 500) { area = PlayerWindow.ContentArea(hwnd); areaAt = now; }
                        var (cx, cy, cw, ch) = Crop(area, frame.Width, frame.Height);
                        var (w, h) = TargetSize(cw, ch, screen.W, screen.H);
                        if (encoder is null || encoder.Width != w || encoder.Height != h)
                        {
                            // first picture, or the window changed shape: a new encoder and a new setup message
                            encoder?.Dispose();
                            encoder = new H264Encoder(w, h, Fps, BitrateFor(w, h, quality));
                            nv12 = new byte[w * h * 3 / 2];
                            announced = false; keyNext = true;
                        }
                        Nv12.Scale(frame.Pixels, frame.Stride, cx, cy, cw, ch, nv12!, w, h);
                    }
                    finally { capture.Release(frame); }
                }
                else if (encoder is null) continue; // nothing drawn yet (a minimized window never draws)
                // a picture that doesn't change brings no new frames: the last one goes again once a second, so the
                // phone's connection never sits idle long enough to time out
                if (frame is null && now - lastFrameMs < 900) continue;
                if (frame is not null && now - lastFrameMs < frameMs - 4) continue; // at most Fps a second
                if (now - lastKeyMs > 1000) keyNext = true; // a phone that fell behind waits at most a second to catch up
                foreach (var (data, key, _) in encoder!.Encode(nv12, now * 1000, keyNext))
                {
                    if (key) { keyNext = false; lastKeyMs = now; }
                    if (!announced)
                    {
                        if (!key || encoder.Codec is not { } codec) continue; // the phone starts on a key frame
                        await Write(body, Setup, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { codec, width = encoder.Width, height = encoder.Height })), 0, ct);
                        announced = true;
                    }
                    await Write(body, key ? KeyFrame : Frame, data, now * 1000, ct);
                }
                lastFrameMs = now;
                await body.FlushAsync(ct);
            }
        }
        finally
        {
            encoder?.Dispose();
            log.LogInformation("screen: stopped");
        }
    }

    /// <summary>The part of the captured picture to send, in pixels: the page area if there is one, else all of it.</summary>
    internal static (int X, int Y, int W, int H) Crop((double X, double Y, double W, double H)? area, int width, int height)
    {
        if (area is not { } a) return (0, 0, width, height);
        var x = (int)Math.Round(a.X * width); var y = (int)Math.Round(a.Y * height);
        var w = Math.Min(width - x, (int)Math.Round(a.W * width)); var h = Math.Min(height - y, (int)Math.Round(a.H * height));
        return w >= 16 && h >= 16 ? (x, y, w, h) : (0, 0, width, height);
    }

    /// <summary>
    /// The picture's size scaled down to fit maxW x maxH (never up), both sides even (NV12 needs it). With the old
    /// width-only limit (maxH == maxW) the height follows from the width.
    /// </summary>
    internal static (int W, int H) TargetSize(int width, int height, int maxW, int maxH)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxW / Math.Max(1, width), (double)maxH / Math.Max(1, height)));
        if (maxH == maxW) scale = Math.Min(1.0, (double)maxW / Math.Max(1, width));
        var w = Math.Max(2, (int)(width * scale) & ~1);
        var h = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        return (w, h);
    }

    private static async Task Write(Stream body, byte type, byte[] data, long timeUs, CancellationToken ct)
    {
        var video = type != Setup;
        var head = new byte[video ? 13 : 5];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(1 + (video ? 8 : 0) + data.Length));
        head[4] = type;
        if (video) BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(5), timeUs);
        await body.WriteAsync(head, ct);
        await body.WriteAsync(data, ct);
    }
#endif
}

/// <summary>BGRA to NV12 (BT.601, limited range: what decoders assume when the stream doesn't say), scaled on the way.</summary>
internal static class Nv12
{
    /// <summary>The source rectangle (sx, sy, sw, sh) of a BGRA picture, scaled to dw x dh into NV12.</summary>
    public static void Scale(byte[] src, int stride, int sx0, int sy0, int sw, int sh, byte[] dst, int dw, int dh)
    {
        // each target pixel averages the 2x2 source pixels at its spot: smooth enough when shrinking a video by half
        var xs = new int[dw];
        for (var x = 0; x < dw; x++) xs[x] = (sx0 + Math.Clamp((int)((x + 0.5) * sw / dw - 0.5), 0, sw - 2)) * 4;
        var uvBase = dw * dh;
        for (var y = 0; y < dh; y++)
        {
            var sy = sy0 + Math.Clamp((int)((y + 0.5) * sh / dh - 0.5), 0, sh - 2);
            var r0 = sy * stride; var r1 = r0 + stride;
            var yRow = y * dw;
            var uvRow = (y & 1) == 0 ? uvBase + (y >> 1) * dw : -1;
            for (var x = 0; x < dw; x++)
            {
                var o = r0 + Math.Max(0, xs[x]);
                var p = r1 + Math.Max(0, xs[x]);
                int b = src[o] + src[o + 4] + src[p] + src[p + 4];
                int g = src[o + 1] + src[o + 5] + src[p + 1] + src[p + 5];
                int r = src[o + 2] + src[o + 6] + src[p + 2] + src[p + 6];
                // sums of four: the coefficients carry the /4
                dst[yRow + x] = (byte)(((66 * r + 129 * g + 25 * b + 512) >> 10) + 16);
                if (uvRow >= 0 && (x & 1) == 0)
                {
                    dst[uvRow + x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 512) >> 10) + 128, 0, 255);
                    dst[uvRow + x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 512) >> 10) + 128, 0, 255);
                }
            }
        }
    }
}
