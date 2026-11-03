using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AlbumWall.Tests;

/// A Subsonic server in a few dozen lines, on 127.0.0.1, for Navidrome.cs:
/// it answers each call from a table of JSON bodies and checks the sign-in
/// the way the API does (t = md5(password + s)).
public sealed class FakeSubsonic : IDisposable
{
    public string Url { get; }
    public string User { get; } = "dave";
    public string Password { get; } = "hunter2-ünïcode";

    /// call name ("getAlbumList2") -> the inside of "subsonic-response".
    public Dictionary<string, string> Answers { get; } = [];
    /// cover id -> (media type, bytes).
    public Dictionary<string, (string Type, byte[] Bytes)> Covers { get; } = [];
    /// Calls answered with this HTTP status instead.
    public Dictionary<string, int> Status { get; } = [];
    public List<string> Asked { get; } = [];

    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeSubsonic()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(Url + "/");
        _http.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            try { Answer(ctx); } catch { ctx.Response.StatusCode = 500; }
            ctx.Response.Close();
        }
    }

    private void Answer(HttpListenerContext ctx)
    {
        var call = ctx.Request.Url!.AbsolutePath.Split('/').Last();
        var q = ctx.Request.QueryString;
        lock (Asked) Asked.Add(call + (q["offset"] is { } o ? $"@{o}" : q["songOffset"] is { } so ? $"@{so}" : ""));
        if (Status.TryGetValue(call, out var status)) { ctx.Response.StatusCode = status; return; }

        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Password + q["s"]))).ToLowerInvariant();
        string inner;
        if (q["u"] != User || q["t"] != token)
            inner = """ "status":"failed","error":{"code":40,"message":"Wrong username or password"} """;
        else if (call == "getCoverArt" && Covers.TryGetValue(q["id"] ?? "", out var cover))
        {
            ctx.Response.ContentType = cover.Type;
            ctx.Response.OutputStream.Write(cover.Bytes);
            return;
        }
        else if (call == "getCoverArt")
            inner = """ "status":"failed","error":{"code":70,"message":"Artwork not found"} """;
        else
            inner = "\"status\":\"ok\"" + (Answers.TryGetValue(call, out var a) ? "," + a : "");
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes("{\"subsonic-response\":{\"version\":\"1.16.1\"," + inner + "}}"));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _http.Stop(); _http.Close(); } catch { }
    }
}
