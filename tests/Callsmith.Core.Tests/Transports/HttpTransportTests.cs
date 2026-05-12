using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Callsmith.Core.Transports.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Transports;

public sealed class HttpTransportTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Creates an <see cref="HttpTransport"/> whose outbound calls are intercepted
    /// by <paramref name="handler"/>.
    /// </summary>
    private static HttpTransport CreateTransport(HttpMessageHandler handler)
        => new(handler, handler, NullLogger<HttpTransport>.Instance);

    /// <summary>Builds a minimal GET request to the given URL.</summary>
    private static RequestModel GetRequest(string url = "https://example.com") =>
        new() { Method = HttpMethod.Get, Url = url };

    // ---------------------------------------------------------------------------
    // SupportedSchemes
    // ---------------------------------------------------------------------------

    [Fact]
    public void SupportedSchemes_ContainsHttpAndHttps()
    {
        var transport = CreateTransport(new StubHandler(HttpStatusCode.OK));

        transport.SupportedSchemes.Should().BeEquivalentTo(["http", "https"]);
    }

    // ---------------------------------------------------------------------------
    // SendAsync — argument validation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var transport = CreateTransport(new StubHandler(HttpStatusCode.OK));

        var act = () => transport.SendAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------------------------------------------------------------------
    // SendAsync — status code and reason phrase
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.OK, "OK")]
    [InlineData(HttpStatusCode.NotFound, "Not Found")]
    [InlineData(HttpStatusCode.InternalServerError, "Internal Server Error")]
    public async Task SendAsync_ReturnsCorrectStatusCodeAndReasonPhrase(
        HttpStatusCode statusCode, string reasonPhrase)
    {
        var handler = new StubHandler(statusCode, reasonPhrase: reasonPhrase);
        var transport = CreateTransport(handler);

        var response = await transport.SendAsync(GetRequest());

        response.StatusCode.Should().Be((int)statusCode);
        response.ReasonPhrase.Should().Be(reasonPhrase);
    }

    // ---------------------------------------------------------------------------
    // SendAsync — response body
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_ReturnsBodyAsStringAndBytes()
    {
        const string bodyContent = """{"message":"hello"}""";
        var handler = new StubHandler(HttpStatusCode.OK, body: bodyContent);
        var transport = CreateTransport(handler);

        var response = await transport.SendAsync(GetRequest());

        response.Body.Should().Be(bodyContent);
        response.BodyBytes.Should().Equal(System.Text.Encoding.UTF8.GetBytes(bodyContent));
        response.BodySizeBytes.Should().Be(bodyContent.Length);
    }

    [Fact]
    public async Task SendAsync_WhenNoBody_ReturnsEmptyBodyAndBytes()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, body: string.Empty);
        var transport = CreateTransport(handler);

        var response = await transport.SendAsync(GetRequest());

        response.Body.Should().BeEmpty();
        response.BodyBytes.Should().BeEmpty();
        response.BodySizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_WithUnknownCharset_FallsBackToUtf8AndDoesNotThrow()
    {
        // Arrange — a legal HTTP response whose Content-Type specifies a charset that
        // .NET cannot resolve.  Prior to the fix this would throw ArgumentException and
        // surface as a transport error even though the HTTP exchange succeeded.
        const string body = "hello";
        var handler = new CustomContentTypeHandler(HttpStatusCode.OK, body,
            contentType: "text/plain; charset=unknown-8bit");
        var transport = CreateTransport(handler);

        // Act — must not throw
        var response = await transport.SendAsync(GetRequest());

        // Assert — body decoded via UTF-8 fallback
        response.Body.Should().Be(body);
        response.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task SendAsync_WithSupportedContentEncoding_DecodesResponseBody(string encoding)
    {
        const string body = """{"message":"héllo"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var encodedBytes = Encode(bodyBytes, encoding);
        var handler = new EncodedContentHandler(HttpStatusCode.OK, encodedBytes, encoding, "application/json; charset=utf-8");
        var transport = CreateTransport(handler);

        var response = await transport.SendAsync(GetRequest());

        response.Body.Should().Be(body);
        response.BodyBytes.Should().Equal(bodyBytes);
    }

    // ---------------------------------------------------------------------------
    // SendAsync — response headers
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_ReturnsResponseHeaders()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            headers: new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = "abc-123",
                ["X-Custom-Header"] = "value",
            });
        var transport = CreateTransport(handler);

        var response = await transport.SendAsync(GetRequest());

        response.Headers.Should().ContainKey("X-Correlation-Id")
            .WhoseValue.Should().Be("abc-123");
        response.Headers.Should().ContainKey("X-Custom-Header")
            .WhoseValue.Should().Be("value");
    }

    // ---------------------------------------------------------------------------
    // SendAsync — elapsed time
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_ElapsedIsPositive()
    {
        var transport = CreateTransport(new StubHandler(HttpStatusCode.OK));

        var response = await transport.SendAsync(GetRequest());

        response.Elapsed.Should().BePositive();
    }

    // ---------------------------------------------------------------------------
    // SendAsync — request forwarding
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SendAsync_ForwardsHttpMethod(string method)
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = new HttpMethod(method),
            Url = "https://example.com",
        };

        await transport.SendAsync(request);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Method.Should().Be(method);
    }

    [Fact]
    public async Task SendAsync_ForwardsRequestHeaders()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Get,
            Url = "https://example.com",
            Headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Api-Key"] = "secret",
            },
        };

        await transport.SendAsync(request);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Should().Contain(h =>
            h.Key == "Accept" && h.Value.Contains("application/json"));
    }

    [Fact]
    public async Task SendAsync_ForwardsRequestBodyAndContentType()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            Body = """{"key":"value"}""",
            ContentType = "application/json",
        };

        await transport.SendAsync(request);

        // Body is captured by the stub before HttpTransport disposes the content.
        handler.LastRequestBody.Should().Be("""{"key":"value"}""");
        handler.LastRequest!.Content!.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task SendAsync_UsesExplicitContentTypeHeader_WhenProvided()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            Body = "plain-text-body",
            ContentType = "application/json",
            Headers = new Dictionary<string, string>
            {
                [WellKnownHeaders.ContentType] = "text/plain",
            },
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content!.Headers.ContentType?.MediaType.Should().Be("text/plain");
    }

    [Theory]
    [InlineData("content-type")]
    [InlineData("CONTENT-TYPE")]
    [InlineData("Content-Type")]
    public async Task SendAsync_ExplicitContentTypeHeader_IsCaseInsensitive(string headerKey)
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            Body = "body",
            ContentType = "application/json",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [headerKey] = "text/csv",
            },
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content!.Headers.ContentType?.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task SendAsync_WithBodyBytes_SendsBinaryBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            BodyBytes = bytes,
            ContentType = CollectionRequest.BodyTypes.FileContentType,
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content.Should().BeOfType<ByteArrayContent>();
        handler.LastRequest!.Content!.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task SendAsync_WithBodyBytes_BodyBytesTakePrecedenceOverBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var bytes = new byte[] { 0xAA, 0xBB };
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            Body = "text-body",
            BodyBytes = bytes,
            ContentType = CollectionRequest.BodyTypes.FileContentType,
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content.Should().BeOfType<ByteArrayContent>();
    }

    [Fact]
    public async Task SendAsync_WithMultipartFormParams_SendsMultipartBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            MultipartFormParams =
            [
                new KeyValuePair<string, string>("field1", "value1"),
                new KeyValuePair<string, string>("field2", "value2"),
            ],
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content.Should().BeOfType<MultipartFormDataContent>();
        handler.LastRequest!.Content!.Headers.ContentType?.MediaType.Should().Be("multipart/form-data");
    }

    [Fact]
    public async Task SendAsync_WithMultipartFormParams_ContentTypeIncludesBoundary()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            MultipartFormParams = [new KeyValuePair<string, string>("key", "val")],
        };

        await transport.SendAsync(request);

        // The boundary parameter must be present in the Content-Type.
        handler.LastRequest!.Content!.Headers.ContentType!.Parameters
            .Should().Contain(p => p.Name == "boundary");
    }

    [Fact]
    public async Task SendAsync_WithMultipartFiles_SendsMultipartFileContent()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler);
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://example.com",
            MultipartFormFiles =
            [
                new MultipartFilePart
                {
                    Key = "file",
                    FileBytes = [0xAA, 0xBB, 0xCC],
                    FileName = "upload.bin",
                },
            ],
        };

        await transport.SendAsync(request);

        handler.LastRequest!.Content.Should().BeOfType<MultipartFormDataContent>();
        handler.LastRequestBody.Should().Contain("upload.bin");
    }

    // ---------------------------------------------------------------------------
    // SendAsync — cancellation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var handler = new NeverRespondingHandler();
        var transport = CreateTransport(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => transport.SendAsync(GetRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------------------
    // Stub helpers
    // ---------------------------------------------------------------------------

    private sealed class StubHandler(
        HttpStatusCode statusCode,
        string body = "",
        string reasonPhrase = "",
        Dictionary<string, string>? headers = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Request body text captured before the content is disposed.</summary>
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;

            // Read and cache the body now — the HttpTransport disposes the response
            // (and transitively the request content) before the test can read it.
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
                ReasonPhrase = reasonPhrase,
            };
            if (headers is not null)
                foreach (var (key, value) in headers)
                    response.Headers.TryAddWithoutValidation(key, value);

            return response;
        }
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Returns a response whose Content-Type is set verbatim (including custom charsets).
    /// </summary>
    private sealed class CustomContentTypeHandler(
        HttpStatusCode statusCode,
        string body,
        string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            var content = new ByteArrayContent(bytes);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            var response = new HttpResponseMessage(statusCode) { Content = content };
            return Task.FromResult(response);
        }
    }

    private sealed class EncodedContentHandler(
        HttpStatusCode statusCode,
        byte[] bodyBytes,
        string contentEncoding,
        string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(bodyBytes);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            content.Headers.TryAddWithoutValidation("Content-Encoding", contentEncoding);
            var response = new HttpResponseMessage(statusCode) { Content = content };
            return Task.FromResult(response);
        }
    }

    private static byte[] Encode(byte[] bytes, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream stream = encoding switch
               {
                   "gzip" => new GZipStream(output, CompressionMode.Compress, leaveOpen: true),
                   "deflate" => new DeflateStream(output, CompressionMode.Compress, leaveOpen: true),
                   "br" => new BrotliStream(output, CompressionMode.Compress, leaveOpen: true),
                   _ => throw new NotSupportedException($"Unsupported test encoding '{encoding}'."),
               })
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }
}
