using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

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
        });

        _noRedirectsClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
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
            var charset = httpResponse.Content.Headers.ContentType?.CharSet;
            var encoding = charset is not null
                ? Encoding.GetEncoding(charset)
                : Encoding.UTF8;
            var bodyString = encoding.GetString(bodyBytes);
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
                BodyBytes = bodyBytes,
                FinalUrl = finalUrl,
                Elapsed = stopwatch.Elapsed,
            };
        }
    }

    private static HttpRequestMessage BuildHttpRequest(RequestModel request)
    {
        var message = new HttpRequestMessage(request.Method, request.Url);

        foreach (var (key, value) in request.Headers)
        {
            // Content headers must be set on the content, not the request.
            if (!message.Headers.TryAddWithoutValidation(key, value))
                message.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        if (request.BodyBytes is not null)
        {
            message.Content = new ByteArrayContent(request.BodyBytes);

            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        else if (request.MultipartFormParams is { Count: > 0 })
        {
            var multipart = new MultipartFormDataContent();
            foreach (var (key, value) in request.MultipartFormParams)
                multipart.Add(new StringContent(value), key);
            message.Content = multipart;
            // Content-Type (including the required boundary parameter) is set automatically.
        }
        else if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body);

            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
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
