// Local modification (not upstream's): the HTTPS calls `/derethnetwork verify` and the answer to the hub's
// challenge make, written to the traffic log. It sits in front of the client's own handler and
// changes nothing it is handed: with the traffic log off it only passes the call on; with it on it
// reads the request's body (a buffered string), and copies at most MaxBody bytes of the answer into a
// fresh content before it records them. HubVerify reads no more than that of an answer, so it reads
// the same bytes either way.

using System.Text;

namespace Dereth.Network;

public sealed class TrafficHttpHandler : DelegatingHandler
{
    /// <summary>The most of an answer's body kept: what HubVerify reads of one.</summary>
    public const int MaxBody = 64 * 1024;

    private readonly Func<TrafficLog?> traffic;

    public TrafficHttpHandler(HttpMessageHandler inner, Func<TrafficLog?> traffic) : base(inner)
    {
        this.traffic = traffic;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var log = traffic();
        if (log is null || !log.Enabled)
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        var url = request.RequestUri?.ToString() ?? "";
        var type = request.RequestUri?.AbsolutePath.EndsWith("/answer", StringComparison.Ordinal) == true ? "https.answer" : "https.verify";
        var sent = "";
        try
        {
            if (request.Content is not null)
                sent = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sent = "(unreadable: " + e.GetType().Name + ")";
        }
        HttpResponseMessage response;
        byte[] body;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.Https(type, url, 0, sent, "(no answer: " + e.GetType().Name + ")");
            throw;
        }
        try
        {
            body = await ReadAtMost(response.Content, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.Https(type, url, (int)response.StatusCode, sent, "(body unreadable: " + e.GetType().Name + ")");
            response.Dispose();
            throw;
        }
        var fresh = new ByteArrayContent(body);
        foreach (var h in response.Content.Headers)
        {
            if (!string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                fresh.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        response.Content.Dispose();
        response.Content = fresh;
        log.Https(type, url, (int)response.StatusCode, sent, Encoding.UTF8.GetString(body));
        return response;
    }

    private static async Task<byte[]> ReadAtMost(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBody];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }
        return buffer.AsSpan(0, read).ToArray();
    }
}
