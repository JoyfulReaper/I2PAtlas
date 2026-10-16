using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;

namespace I2PAtlas.Tests;

internal static class CrawlTestData
{
    internal const string ClassicB32 = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzq.b32.i2p";
    internal const string Allow = "User-agent: *\nAllow: /";
    internal static readonly DateTimeOffset Started = new(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
    internal static I2pSeed Seed(string host, int salt = 0) => new(host, SeedSnapshotTestData.Destination(salt, 4));
    internal static string B32(I2pSeed seed) => I2pSeedIdentity.FromSeed(seed).DestinationHostname;
    internal static string Snapshot(TemporaryFiles files, params I2pSeed[] seeds)
    {
        var root = SeedSnapshotTestData.Snapshot();
        root["seeds"] = new JsonArray(seeds.Select(s => (JsonNode)new JsonObject { ["hostname"] = s.Hostname, ["destination"] = s.Destination }).ToArray());
        return files.Write("seeds.json", root.ToJsonString());
    }
    internal static I2pProbeObservation Observation(params string[] urls) => new(
        I2pRequestOutcome.ProxyHttpResponse, I2pOriginEvidence.Unproven, I2pProxyResponseClassification.LikelyOriginResponse,
        new(200, "text/plain", 20, null, false), I2pRobotsObservation.AllowsRoot, I2pHomepageDisposition.ResponseObserved,
        I2pRequestOutcome.ProxyHttpResponse, new(200, "text/html", 100, null, false),
        new("text/html", 100, false, "PRIVATE_TITLE", urls.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(u => new I2pDiscoveredLink(u, I2pDiscoveredLinkKind.I2p)).ToImmutableArray(), false), I2pProxyResponseClassification.Ambiguous);
    internal static I2pRawCrawlArtifact Artifact()
    {
        var a = Seed("a.i2p");
        var b = Seed("b.i2p", 1);
        var evidence = I2pCrawlEvidence.FromObservation(Observation("http://child.i2p/path", $"http://{ClassicB32}/"), Started.AddSeconds(1), Started.AddSeconds(2), 100);
        var hostEvidence = I2pCrawlEvidence.FromObservation(Observation("http://terminal-link.i2p/private?q=secret"), Started.AddSeconds(3), Started.AddSeconds(4), 100);
        var known = ImmutableArray.Create(new I2pCrawlKnownResult(a.Hostname, a.Destination, B32(a), 0, evidence),
            new I2pCrawlKnownResult(b.Hostname, b.Destination, B32(b), 0, evidence));
        var hosts = ImmutableArray.Create(new I2pCrawlHostResult("child.i2p", I2PAtlas.I2pTransport.I2pDiscoveredHostForm.Friendly, 1, I2pCrawlReferrer.FromSeed(a), hostEvidence),
            new I2pCrawlHostResult(ClassicB32, I2PAtlas.I2pTransport.I2pDiscoveredHostForm.ClassicBase32, 1, I2pCrawlReferrer.FromSeed(a), hostEvidence));
        return new(I2pRawCrawlArtifact.CurrentSchema, 1, Started, Started.AddSeconds(5),
            new("i2patlas.seed-snapshot", 1, new string('A', 64), 1234, Started.AddDays(-1), new string('B', 64), 2345),
            new("127.0.0.1", 4444), new(2, 10000, 2, 2), 3, new(4, 2, false), known, hosts,
            I2pCrawlMetrics.Calculate(known.Select(r => r.Evidence).Concat(hosts.Select(r => r.Evidence)), 5000, 2));
    }
    internal static byte[] Response(int status, string body, string type = "text/html") => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 {status} Test\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
    internal static string Json(I2pRawCrawlArtifact artifact) => JsonSerializer.Serialize(artifact, I2pRawCrawlFile.JsonOptions);
}

internal sealed class CrawlProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> connections = [];
    private readonly Task accepting;
    private int active;
    private int maximum;
    internal int MaximumActive => maximum;
    internal string Uri { get; }
    internal ConcurrentQueue<string[]> Requests { get; } = new();
    internal CrawlProxy(Func<Uri, CancellationToken, Task<byte[]?>> response)
    {
        listener.Start();
        Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        accepting = AcceptAsync(response);
    }
    private async Task AcceptAsync(Func<Uri, CancellationToken, Task<byte[]?>> response)
    {
        try
        {
            while (!stop.IsCancellationRequested)
                connections.Add(ServeAsync(await listener.AcceptTcpClientAsync(stop.Token), response));
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException) { }
    }
    private async Task ServeAsync(TcpClient client, Func<Uri, CancellationToken, Task<byte[]?>> response)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var lines = new List<string>();
            var size = 0;
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
            {
                if ((size += line.Length) > 8192) throw new AssertFailedException("Oversized request.");
                lines.Add(line);
            }
            if (lines.Count == 0) return;
            Requests.Enqueue(lines.ToArray());
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = maximum; } while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            byte[]? bytes;
            try { bytes = await response(new Uri(lines[0].Split(' ')[1]), stop.Token); }
            finally { Interlocked.Decrement(ref active); }
            if (bytes == null) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            else await stream.WriteAsync(bytes, stop.Token);
        }
        catch (Exception ex) when (ex is IOException || stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }
}
