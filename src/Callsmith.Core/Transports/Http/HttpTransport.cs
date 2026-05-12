using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Callsmith.Core.Transports.Http;

/// <summary>
/// <see cref="ITransport"/> implementation for HTTP and HTTPS using
/// <see cref="System.Net.Http.HttpClient"/>.
/// </summary>
public sealed class HttpTransport : ITransport, IDisposable
{
    // HttpClient is intentionally reused across requests to avoid socket exhaustion.
    private readonly HttpClient _followRedirectsClient;
    private readonly HttpClient _noRedirectsClient;
    private readonly ILogger<HttpTransport> _logger;

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedSchemes { get; } = ["http", "https"];

    /// <summary>
    /// Initialises a new <see cref="HttpTransport"/> with the provided
    /// <see cref="ILogger{HttpTransport}"/>.
    /// </summary>
    public HttpTransport(ILogger<HttpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        _followRedirectsClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
        });

        _noRedirectsClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        });
    }

    /// <summary>
    /// Internal constructor for unit testing — accepts pre-built <see cref="HttpMessageHandler"/>
    /// instances so tests can intercept outbound requests.
    /// </summary>
    internal HttpTransport(
        HttpMessageHandler followRedirectsHandler,
        HttpMessageHandler noRedirectsHandler,
        ILogger<HttpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(followRedirectsHandler);
        ArgumentNullException.ThrowIfNull(noRedirectsHandler);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _followRedirectsClient = new HttpClient(followRedirectsHandler);
        _noRedirectsClient = new HttpClient(noRedirectsHandler);
    }

    /// <inheritdoc/>
    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = request.FollowRedirects ? _followRedirectsClient : _noRedirectsClient;
        using var httpRequest = BuildHttpRequest(request);

        _logger.LogDebug("Sending {Method} {Url}", request.Method, request.Url);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage httpResponse;

        if (request.Timeout.HasValue)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout.Value);
            httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        }
        else
        {
            httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        }

        using (httpResponse)
        {
            stopwatch.Stop();

            var bodyBytes = await httpResponse.Content.ReadAsByteArrayAsync(ct);
            var decodedBodyBytes = await DecodeContentEncodingAsync(httpResponse, bodyBytes, ct);
            var charset = httpResponse.Content.Headers.ContentType?.CharSet;
            Encoding encoding;
            if (charset is not null)
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // An unknown or unsupported charset name is a legal HTTP response.
                    // Fall back to UTF-8 rather than surfacing a transport error for what
                    // is effectively a content-negotiation issue.
                    _logger.LogDebug(
                        "Unrecognised response charset '{Charset}'; falling back to UTF-8.", charset);
                    encoding = Encoding.UTF8;
                }
            }
            else
            {
                encoding = Encoding.UTF8;
            }
            var bodyString = encoding.GetString(decodedBodyBytes);
            var headers = ReadHeaders(httpResponse);
            var finalUrl = httpResponse.RequestMessage?.RequestUri?.ToString() ?? request.Url;

            _logger.LogDebug(
                "Received {StatusCode} from {Url} in {ElapsedMs}ms",
                (int)httpResponse.StatusCode,
                finalUrl,
                stopwatch.ElapsedMilliseconds);

            return new ResponseModel
            {
                StatusCode = (int)httpResponse.StatusCode,
                ReasonPhrase = httpResponse.ReasonPhrase ?? string.Empty,
                Headers = headers,
                Body = bodyString,
                BodyBytes = decodedBodyBytes,
                BodySizeBytes = bodyBytes.LongLength,
                FinalUrl = finalUrl,
                Elapsed = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>
    /// Decodes response bytes according to the <c>Content-Encoding</c> header values.
    /// Encodings are removed in reverse order to mirror HTTP encoding application.
    /// Returns the original bytes when no supported decoding path is available.
    /// </summary>
    private async Task<byte[]> DecodeContentEncodingAsync(
        HttpResponseMessage response,
        byte[] bodyBytes,
        CancellationToken ct)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0)
            return bodyBytes;

        var encodingStack = encodings
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        if (encodingStack.Length == 0)
            return bodyBytes;

        var decoded = bodyBytes;
        for (var i = encodingStack.Length - 1; i >= 0; i--)
        {
            var contentEncoding = encodingStack[i];
            if (contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                decoded = contentEncoding.ToLowerInvariant() switch
                {
                    "gzip" or "x-gzip" => await DecompressAsync(decoded, bytes => new GZipStream(bytes, CompressionMode.Decompress, leaveOpen: false), ct),
                    "deflate" or "x-deflate" => await DecompressAsync(decoded, bytes => new DeflateStream(bytes, CompressionMode.Decompress, leaveOpen: false), ct),
                    "br" => await DecompressAsync(decoded, bytes => new BrotliStream(bytes, CompressionMode.Decompress, leaveOpen: false), ct),
                    "zlib" => await DecompressAsync(decoded, bytes => new ZLibStream(bytes, CompressionMode.Decompress, leaveOpen: false), ct),
                    "zstd" or "x-zstd" => await DecompressAsync(decoded, bytes => new DecompressionStream(bytes, leaveOpen: false), ct),
                    _ => throw new NotSupportedException($"Unsupported content encoding '{contentEncoding}'."),
                };
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to decode response body with content-encoding '{ContentEncoding}'. Returning raw bytes.",
                    contentEncoding);
                return bodyBytes;
            }
        }

        return decoded;
    }

    /// <summary>
    /// Decompresses a byte array using the provided decompression stream factory.
    /// </summary>
    private static async Task<byte[]> DecompressAsync(
        byte[] bytes,
        Func<MemoryStream, Stream> streamFactory,
        CancellationToken ct)
    {
        using var input = new MemoryStream(bytes);
        using var compressed = streamFactory(input);
        using var output = new MemoryStream();
        await compressed.CopyToAsync(output, ct);
        return output.ToArray();
    }

    private static HttpRequestMessage BuildHttpRequest(RequestModel request)
    {
        var message = new HttpRequestMessage(request.Method, request.Url);
        var deferredContentHeaders = new List<KeyValuePair<string, string>>();

        foreach (var (key, value) in request.Headers)
        {
            // Content headers must be set on the content, not the request.
            if (!message.Headers.TryAddWithoutValidation(key, value))
                deferredContentHeaders.Add(new KeyValuePair<string, string>(key, value));
        }

        if (request.BodyBytes is not null)
        {
            message.Content = new ByteArrayContent(request.BodyBytes);

            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        else if (request.MultipartFormParams is { Count: > 0 } ||
                 request.MultipartFormFiles is { Count: > 0 })
        {
            var multipart = new MultipartFormDataContent();
            if (request.MultipartFormParams is not null)
            {
                foreach (var (key, value) in request.MultipartFormParams)
                    multipart.Add(new StringContent(value), key);
            }

            if (request.MultipartFormFiles is not null)
            {
                foreach (var file in request.MultipartFormFiles.Where(f => !string.IsNullOrWhiteSpace(f.Key)))
                {
                    var content = new ByteArrayContent(file.FileBytes);
                    multipart.Add(content, file.Key, string.IsNullOrWhiteSpace(file.FileName) ? "file" : file.FileName);
                }
            }

            message.Content = multipart;
            // Content-Type (including the required boundary parameter) is set automatically.
        }
        else if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body);

            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }

        if (message.Content is not null)
        {
            foreach (var (key, value) in deferredContentHeaders)
            {
                if (key.Equals(WellKnownHeaders.ContentType, StringComparison.OrdinalIgnoreCase))
                {
                    if (MediaTypeHeaderValue.TryParse(value, out var parsed))
                        message.Content.Headers.ContentType = parsed;
                    else
                        message.Content.Headers.TryAddWithoutValidation(WellKnownHeaders.ContentType, value);
                }
                else
                    message.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return message;
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);

        foreach (var header in response.Content.Headers)
            headers[header.Key] = string.Join(", ", header.Value);

        return headers;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _followRedirectsClient.Dispose();
        _noRedirectsClient.Dispose();
    }
}
