using System.Net.Http;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using FluentAssertions;

namespace Callsmith.Core.Tests;

public sealed class CollectionRequestEqualityComparerTests
{
    [Fact]
    public void Equals_WhenRequestsMatch_ReturnsTrue()
    {
        var left = CreateRequest();
        var right = CreateRequest();

        CollectionRequestEqualityComparer.Instance.Equals(left, right).Should().BeTrue();
    }

    [Fact]
    public void Equals_WhenHeaderOrderDiffers_ReturnsFalse()
    {
        var left = CreateRequest();
        var right = CreateRequest(
            headers: [left.Headers[1], left.Headers[0]]);

        CollectionRequestEqualityComparer.Instance.Equals(left, right).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenMultipartFileBytesDiffer_ReturnsFalse()
    {
        var left = CreateRequest();
        var right = CreateRequest(
            multipartFormFiles:
            [
                new MultipartFilePart
                {
                    Key = "upload",
                    FileBytes = [9, 9, 9],
                    FileName = "photo.png",
                    FilePath = @"c:\tmp\photo.png",
                },
            ]);

        CollectionRequestEqualityComparer.Instance.Equals(left, right).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenPathParamsDifferOnlyByInsertionOrder_ReturnsTrue()
    {
        var left = CreateRequest();
        var right = CreateRequest(
            pathParams: new Dictionary<string, string>
            {
                ["tenant"] = "blue",
                ["id"] = "42",
            });

        CollectionRequestEqualityComparer.Instance.Equals(left, right).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_WhenPathParamsDifferOnlyByInsertionOrder_AreEqual()
    {
        var left = CreateRequest();
        var right = CreateRequest(
            pathParams: new Dictionary<string, string>
            {
                ["tenant"] = "blue",
                ["id"] = "42",
            });

        CollectionRequestEqualityComparer.Instance.GetHashCode(left)
            .Should().Be(CollectionRequestEqualityComparer.Instance.GetHashCode(right));
    }

    [Fact]
    public void Equals_WhenBodyContentsDifferOnlyByDictionaryInsertionOrder_ReturnsTrue()
    {
        var left = CreateRequest();
        var right = CreateRequest(
            allBodyContents: new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Text] = "plain text",
                [CollectionRequest.BodyTypes.Json] = "{\"value\":1}",
            });

        CollectionRequestEqualityComparer.Instance.Equals(left, right).Should().BeTrue();
    }

    private static CollectionRequest CreateRequest(
        IReadOnlyList<RequestKv>? headers = null,
        IReadOnlyDictionary<string, string>? pathParams = null,
        IReadOnlyList<RequestKv>? queryParams = null,
        IReadOnlyDictionary<string, string>? allBodyContents = null,
        IReadOnlyList<KeyValuePair<string, string>>? formParams = null,
        IReadOnlyList<MultipartFilePart>? multipartFormFiles = null,
        IReadOnlyList<MultipartBodyEntry>? multipartBodyEntries = null) => new()
    {
        FilePath = @"c:\collections\sample.callsmith",
        Name = "sample",
        RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Method = HttpMethod.Post,
        Url = "https://api.example.com/users/{id}",
        Description = "request description",
        Headers = headers ?? [new RequestKv("X-One", "1"), new RequestKv("X-Two", "2", false)],
        PathParams = pathParams ?? new Dictionary<string, string>
        {
            ["id"] = "42",
            ["tenant"] = "blue",
        },
        QueryParams = queryParams ?? [new RequestKv("page", "1"), new RequestKv("include", "roles", false)],
        BodyType = CollectionRequest.BodyTypes.Multipart,
        Body = null,
        AllBodyContents = allBodyContents ?? new Dictionary<string, string>
        {
            [CollectionRequest.BodyTypes.Json] = "{\"value\":1}",
            [CollectionRequest.BodyTypes.Text] = "plain text",
        },
        FormParams = formParams ?? [new KeyValuePair<string, string>("name", "alice")],
        MultipartFormFiles = multipartFormFiles ??
        [
            new MultipartFilePart
            {
                Key = "upload",
                FileBytes = [1, 2, 3],
                FileName = "photo.png",
                FilePath = @"c:\tmp\photo.png",
            },
        ],
        MultipartBodyEntries = multipartBodyEntries ??
        [
            new MultipartBodyEntry
            {
                Key = "name",
                TextValue = "alice",
                IsFile = false,
            },
            new MultipartBodyEntry
            {
                Key = "upload",
                IsFile = true,
                FileName = "photo.png",
                FilePath = @"c:\tmp\photo.png",
            },
        ],
        FileBodyBase64 = "AQID",
        FileBodyName = "payload.bin",
        Auth = new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.ApiKey,
            ApiKeyName = "X-Api-Key",
            ApiKeyValue = "secret",
            ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
        },
    };
}