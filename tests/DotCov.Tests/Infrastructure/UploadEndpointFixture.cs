using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TUnit.Core.Interfaces;

namespace DotCov.Tests.Infrastructure;

public sealed record UploadRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

public sealed class UploadEndpointFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<UploadRequest> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpStatusCode?> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpListener? _listener;
    private Task _server = Task.CompletedTask;

    public Uri Url { get; private set; } = null!;
    public Task<UploadRequest> Received => _received.Task;

    public Task InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/coverage");
        _server = ServeAsync(_listener);
        return Task.CompletedTask;
    }

    public void RespondWith(HttpStatusCode status) => SetResponse(status);
    public void DisconnectAfterRequest() => SetResponse(null);

    private void SetResponse(HttpStatusCode? status)
    {
        if (!_response.TrySetResult(status)) throw new InvalidOperationException("A response is already configured.");
    }

    private async Task ServeAsync(TcpListener listener)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(_stop.Token);
            var stream = client.GetStream();
            var headers = await ReadHeadersAsync(stream, _stop.Token);
            var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines[0].Split(' ', 3);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Malformed HTTP request header.");
                fields.Add(line[..colon], line[(colon + 1)..].Trim());
            }

            // The CLI sends StringContent with a known byte length; this is not a general HTTP server.
            if (!fields.TryGetValue("Content-Length", out var rawLength)
                || !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length > 1_048_576)
                throw new InvalidDataException("Expected a bounded Content-Length upload.");

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, _stop.Token);
            _received.SetResult(new UploadRequest(requestLine[0], requestLine[1], fields, Encoding.UTF8.GetString(body)));

            if (await _response.Task.WaitAsync(_stop.Token) is { } status)
            {
                var reply = Encoding.ASCII.GetBytes(FormattableString.Invariant(
                    $"HTTP/1.1 {(int)status} Test response\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(reply, _stop.Token);
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested
            && ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            _received.TrySetCanceled(_stop.Token);
        }
        catch (Exception ex)
        {
            _received.TrySetException(ex);
            throw;
        }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        List<byte> bytes = [];
        var next = new byte[1];
        while (bytes.Count < 16_384)
        {
            if (await stream.ReadAsync(next, ct) is 0) throw new EndOfStreamException("Incomplete HTTP headers.");
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("HTTP headers exceeded the fixture limit.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        try { await _server; }
        finally { _stop.Dispose(); }
    }
}
