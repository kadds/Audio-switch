using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioSwitch_WinUI;

public sealed record HttpStateMessage(
    string ProcessName,
    string Address,
    string Title,
    string StatusText,
    string State);

public sealed record HttpStateDispatchResult(
    int StatusCode,
    bool Matched,
    bool Queued,
    string Message,
    string? RuleId = null,
    string? RuleName = null);

/// <summary>
/// Small, dependency-free HTTP listener for browser/local integrations.
/// TcpListener is used instead of HttpListener so binding 127.0.0.1 or
/// 0.0.0.0 does not require an HTTP URL ACL registration.
/// </summary>
public sealed class LocalHttpStateServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IPAddress bindAddress;
    private readonly int port;
    private readonly string password;
    private readonly Func<HttpStateMessage, Task<HttpStateDispatchResult>> dispatchAsync;
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;

    public LocalHttpStateServer(
        string bindAddress,
        int port,
        string? password,
        Func<HttpStateMessage, Task<HttpStateDispatchResult>> dispatchAsync)
    {
        this.bindAddress = ParseBindAddress(bindAddress);
        this.port = port is >= 1 and <= 65535
            ? port
            : throw new ArgumentOutOfRangeException(nameof(port), "HTTP port must be between 1 and 65535.");
        this.password = password?.Trim() ?? string.Empty;
        this.dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        BindAddress = this.bindAddress.Equals(IPAddress.Any) ? "0.0.0.0" : "127.0.0.1";
    }

    public string BindAddress { get; }
    public int Port => port;
    public bool IsRunning => listener != null;

    public void Start()
    {
        if (listener != null) return;

        var newCancellation = new CancellationTokenSource();
        var newListener = new TcpListener(bindAddress, port);
        try
        {
            newListener.Start();
            listener = newListener;
            cancellation = newCancellation;
            _ = AcceptLoopAsync(newListener, newCancellation.Token);
        }
        catch
        {
            newCancellation.Dispose();
            newListener.Stop();
            throw;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? currentCancellation = Interlocked.Exchange(ref cancellation, null);
        TcpListener? currentListener = Interlocked.Exchange(ref listener, null);
        currentCancellation?.Cancel();
        currentListener?.Stop();
        currentCancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener currentListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await currentListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                (string method, string path, Dictionary<string, string> headers, byte[] initialBody) =
                    await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);

                if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 204, string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                string route = path.Split('?', 2)[0];
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route, "/api/health", StringComparison.OrdinalIgnoreCase))
                {
                    string health = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        listener = "AudioSwitch",
                        endpoint = "/api/state"
                    }, JsonOptions);
                    await WriteResponseAsync(stream, 200, health, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(route, "/api/state", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(route, "/state", StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteJsonAsync(stream, 404, new { ok = false, message = "Use POST /api/state." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!IsAuthorized(headers))
                {
                    await WriteJsonAsync(stream, 401, new { ok = false, message = "X-AudioSwitch-Password is required or incorrect." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                byte[] body;
                if (headers.TryGetValue("Content-Length", out string? contentLengthText))
                {
                    if (!int.TryParse(contentLengthText, out int contentLength) || contentLength < 0 || contentLength > MaxBodyBytes)
                    {
                        await WriteJsonAsync(stream, 400, new { ok = false, message = "A valid Content-Length up to 256 KiB is required." }, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    body = await ReadBodyAsync(stream, initialBody, contentLength, cancellationToken).ConfigureAwait(false);
                }
                else if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding) &&
                         transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    body = await ReadChunkedBodyAsync(stream, initialBody, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteJsonAsync(stream, 400, new { ok = false, message = "Content-Length or chunked Transfer-Encoding is required." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                HttpStateRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<HttpStateRequest>(body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    await WriteJsonAsync(stream, 400, new { ok = false, message = "Invalid JSON.", detail = ex.Message }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (request == null || string.IsNullOrWhiteSpace(request.ProcessName))
                {
                    await WriteJsonAsync(stream, 400, new { ok = false, message = "processName is required." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                HttpStateDispatchResult result = await dispatchAsync(
                    new HttpStateMessage(
                        request.ProcessName.Trim(),
                        request.Address?.Trim() ?? string.Empty,
                        request.Title?.Trim() ?? string.Empty,
                        request.StatusText?.Trim() ?? string.Empty,
                        request.State?.Trim() ?? string.Empty)).ConfigureAwait(false);
                await WriteJsonAsync(stream, result.StatusCode, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    using NetworkStream stream = client.GetStream();
                    await WriteJsonAsync(stream, 500, new { ok = false, message = "AudioSwitch HTTP listener failed.", detail = ex.Message }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] InitialBody)> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var received = new List<byte>(4096);
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        while (received.Count <= MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The HTTP client closed the connection before sending headers.");
            for (int index = 0; index < read; index++) received.Add(buffer[index]);
            headerEnd = FindHeaderEnd(received);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) throw new InvalidDataException("HTTP headers are too large or incomplete.");

        byte[] allBytes = received.ToArray();
        string headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) throw new InvalidDataException("Invalid HTTP request line.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            int separator = lines[index].IndexOf(':');
            if (separator <= 0) continue;
            headers[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
        }

        byte[] initialBody = allBytes[(headerEnd + 4)..];
        return (requestLine[0], requestLine[1], headers, initialBody);
    }

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream,
        byte[] initialBody,
        int contentLength,
        CancellationToken cancellationToken)
    {
        byte[] body = new byte[contentLength];
        int copied = Math.Min(initialBody.Length, contentLength);
        Buffer.BlockCopy(initialBody, 0, body, 0, copied);
        int offset = copied;
        while (offset < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The HTTP client closed the connection before sending the body.");
            offset += read;
        }
        return body;
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] initialBody,
        CancellationToken cancellationToken)
    {
        var buffered = new List<byte>(initialBody);
        int offset = 0;
        var body = new List<byte>();

        async Task EnsureAsync(int count)
        {
            while (buffered.Count - offset < count)
            {
                byte[] readBuffer = new byte[4096];
                int read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The HTTP client closed a chunked request early.");
                for (int index = 0; index < read; index++) buffered.Add(readBuffer[index]);
            }
        }

        async Task<string> ReadLineAsync()
        {
            while (true)
            {
                for (int index = offset; index + 1 < buffered.Count; index++)
                {
                    if (buffered[index] != '\r' || buffered[index + 1] != '\n') continue;
                    string line = Encoding.ASCII.GetString(buffered.Skip(offset).Take(index - offset).ToArray());
                    offset = index + 2;
                    return line;
                }
                await EnsureAsync(buffered.Count - offset + 1).ConfigureAwait(false);
            }
        }

        while (true)
        {
            string sizeLine = await ReadLineAsync().ConfigureAwait(false);
            string sizeText = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
            {
                throw new InvalidDataException("Invalid chunk size.");
            }

            if (size == 0)
            {
                // Consume optional trailer headers until the empty line.
                while (!string.IsNullOrEmpty(await ReadLineAsync().ConfigureAwait(false))) { }
                return body.ToArray();
            }

            if (body.Count + size > MaxBodyBytes) throw new InvalidDataException("The HTTP request body is too large.");
            await EnsureAsync(size + 2).ConfigureAwait(false);
            body.AddRange(buffered.GetRange(offset, size));
            offset += size;
            if (buffered[offset] != '\r' || buffered[offset + 1] != '\n') throw new InvalidDataException("Invalid chunk terminator.");
            offset += 2;
        }
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(value, JsonOptions);
        await WriteResponseAsync(stream, statusCode, body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string body, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            500 => "Internal Server Error",
            _ => "Error"
        };
        string headers = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                         "Content-Type: application/json; charset=utf-8\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                         "Access-Control-Allow-Headers: Content-Type, X-AudioSwitch-Password\r\n" +
                         "Connection: close\r\n" +
                         $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (int index = 3; index < bytes.Count; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' && bytes[index - 1] == '\r' && bytes[index] == '\n')
            {
                return index - 3;
            }
        }
        return -1;
    }

    private static IPAddress ParseBindAddress(string value) => value.Trim() switch
    {
        "127.0.0.1" => IPAddress.Loopback,
        "0.0.0.0" => IPAddress.Any,
        _ => throw new ArgumentException("HTTP bind address must be 127.0.0.1 or 0.0.0.0.", nameof(value))
    };

    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers)
    {
        if (password.Length == 0) return true;
        if (!headers.TryGetValue("X-AudioSwitch-Password", out string? supplied)) return false;

        byte[] expectedBytes = Encoding.UTF8.GetBytes(password);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private sealed class HttpStateRequest
    {
        public string? ProcessName { get; set; }
        public string? Address { get; set; }
        public string? Title { get; set; }
        public string? StatusText { get; set; }
        public string? State { get; set; }
    }
}
