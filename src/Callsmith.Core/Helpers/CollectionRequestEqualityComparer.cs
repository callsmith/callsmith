using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Strict value comparer for persisted request editor state.
/// Used by the desktop editor to decide whether the current in-memory request
/// still matches the last saved baseline.
/// </summary>
public sealed class CollectionRequestEqualityComparer : IEqualityComparer<CollectionRequest>
{
    public static CollectionRequestEqualityComparer Instance { get; } = new();

    public bool Equals(CollectionRequest? x, CollectionRequest? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;

        return x.RequestId == y.RequestId
            && string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal)
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal)
            && string.Equals(x.Method.Method, y.Method.Method, StringComparison.Ordinal)
            && string.Equals(x.Url, y.Url, StringComparison.Ordinal)
            && string.Equals(x.Description, y.Description, StringComparison.Ordinal)
            && EqualSequence(x.Headers, y.Headers)
            && EqualPathParams(x.PathParams, y.PathParams)
            && EqualSequence(x.QueryParams, y.QueryParams)
            && string.Equals(x.BodyType, y.BodyType, StringComparison.Ordinal)
            && string.Equals(x.Body, y.Body, StringComparison.Ordinal)
            && EqualBodyContents(x, y)
            && EqualSequence(x.FormParams, y.FormParams)
            && EqualSequence(x.MultipartFormFiles, y.MultipartFormFiles, MultipartFilePartEquals)
            && EqualSequence(x.MultipartBodyEntries, y.MultipartBodyEntries, MultipartBodyEntryEquals)
            && string.Equals(x.FileBodyBase64, y.FileBodyBase64, StringComparison.Ordinal)
            && string.Equals(x.FileBodyName, y.FileBodyName, StringComparison.Ordinal)
            && AuthEquals(x.Auth, y.Auth);
    }

    public int GetHashCode(CollectionRequest obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var hash = new HashCode();
        hash.Add(obj.RequestId);
        hash.Add(obj.FilePath, StringComparer.Ordinal);
        hash.Add(obj.Name, StringComparer.Ordinal);
        hash.Add(obj.Method.Method, StringComparer.Ordinal);
        hash.Add(obj.Url, StringComparer.Ordinal);
        hash.Add(obj.Description, StringComparer.Ordinal);
        AddSequenceHash(hash, obj.Headers);
        AddPathParamsHash(hash, obj.PathParams);
        AddSequenceHash(hash, obj.QueryParams);
        hash.Add(obj.BodyType, StringComparer.Ordinal);
        hash.Add(obj.Body, StringComparer.Ordinal);
        AddBodyContentsHash(hash, obj);
        AddSequenceHash(hash, obj.FormParams);
        AddSequenceHash(hash, obj.MultipartFormFiles, MultipartFilePartHash);
        AddSequenceHash(hash, obj.MultipartBodyEntries, MultipartBodyEntryHash);
        hash.Add(obj.FileBodyBase64, StringComparer.Ordinal);
        hash.Add(obj.FileBodyName, StringComparer.Ordinal);
        AddAuthHash(hash, obj.Auth);
        return hash.ToHashCode();
    }

    private static bool AuthEquals(AuthConfig x, AuthConfig y) =>
        string.Equals(x.AuthType, y.AuthType, StringComparison.Ordinal)
        && string.Equals(x.Token, y.Token, StringComparison.Ordinal)
        && string.Equals(x.Username, y.Username, StringComparison.Ordinal)
        && string.Equals(x.Password, y.Password, StringComparison.Ordinal)
        && string.Equals(x.ApiKeyName, y.ApiKeyName, StringComparison.Ordinal)
        && string.Equals(x.ApiKeyValue, y.ApiKeyValue, StringComparison.Ordinal)
        && string.Equals(x.ApiKeyIn, y.ApiKeyIn, StringComparison.Ordinal);

    private static bool EqualPathParams(
        IReadOnlyDictionary<string, string> x,
        IReadOnlyDictionary<string, string> y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x.Count != y.Count) return false;

        using var xEnumerator = x.GetEnumerator();
        using var yEnumerator = y.GetEnumerator();
        while (xEnumerator.MoveNext())
        {
            if (!yEnumerator.MoveNext())
                return false;

            if (!string.Equals(xEnumerator.Current.Key, yEnumerator.Current.Key, StringComparison.Ordinal)
                || !string.Equals(xEnumerator.Current.Value, yEnumerator.Current.Value, StringComparison.Ordinal))
                return false;
        }

        return !yEnumerator.MoveNext();
    }

    private static bool EqualBodyContents(
        CollectionRequest x,
        CollectionRequest y)
    {
        var normalizedX = NormalizeBodyContents(x);
        var normalizedY = NormalizeBodyContents(y);

        if (ReferenceEquals(normalizedX, normalizedY)) return true;
        if (normalizedX.Count != normalizedY.Count) return false;

        foreach (var pair in normalizedX)
        {
            if (!normalizedY.TryGetValue(pair.Key, out var otherValue))
                return false;

            if (!string.Equals(pair.Value, otherValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool MultipartFilePartEquals(MultipartFilePart x, MultipartFilePart y) =>
        string.Equals(x.Key, y.Key, StringComparison.Ordinal)
        && x.FileBytes.AsSpan().SequenceEqual(y.FileBytes)
        && string.Equals(x.FileName, y.FileName, StringComparison.Ordinal)
        && string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal)
        && x.IsEnabled == y.IsEnabled;

    private static bool MultipartBodyEntryEquals(MultipartBodyEntry x, MultipartBodyEntry y) =>
        string.Equals(x.Key, y.Key, StringComparison.Ordinal)
        && x.IsFile == y.IsFile
        && string.Equals(x.TextValue, y.TextValue, StringComparison.Ordinal)
        && string.Equals(x.FileName, y.FileName, StringComparison.Ordinal)
        && string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal)
        && x.IsEnabled == y.IsEnabled;

    private static bool EqualSequence<T>(IReadOnlyList<T> x, IReadOnlyList<T> y)
        where T : notnull
        => EqualSequence(x, y, EqualityComparer<T>.Default.Equals);

    private static bool EqualSequence<T>(
        IReadOnlyList<T> x,
        IReadOnlyList<T> y,
        Func<T, T, bool> itemEquals)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x.Count != y.Count) return false;

        for (var index = 0; index < x.Count; index++)
        {
            if (!itemEquals(x[index], y[index]))
                return false;
        }

        return true;
    }

    private static void AddAuthHash(HashCode hash, AuthConfig auth)
    {
        hash.Add(auth.AuthType, StringComparer.Ordinal);
        hash.Add(auth.Token, StringComparer.Ordinal);
        hash.Add(auth.Username, StringComparer.Ordinal);
        hash.Add(auth.Password, StringComparer.Ordinal);
        hash.Add(auth.ApiKeyName, StringComparer.Ordinal);
        hash.Add(auth.ApiKeyValue, StringComparer.Ordinal);
        hash.Add(auth.ApiKeyIn, StringComparer.Ordinal);
    }

    private static void AddPathParamsHash(HashCode hash, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }
    }

    private static void AddBodyContentsHash(HashCode hash, CollectionRequest request)
    {
        foreach (var pair in NormalizeBodyContents(request).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> NormalizeBodyContents(CollectionRequest request)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in request.AllBodyContents)
        {
            if (!string.IsNullOrEmpty(pair.Value))
                normalized[pair.Key] = pair.Value;
        }

        if (request.BodyType is CollectionRequest.BodyTypes.Json
                           or CollectionRequest.BodyTypes.Text
                           or CollectionRequest.BodyTypes.Xml
                           or CollectionRequest.BodyTypes.Yaml
                           or CollectionRequest.BodyTypes.Other
            && !string.IsNullOrEmpty(request.Body))
        {
            normalized[request.BodyType] = request.Body;
        }

        return normalized;
    }

    private static int MultipartFilePartHash(MultipartFilePart value)
    {
        var hash = new HashCode();
        hash.Add(value.Key, StringComparer.Ordinal);
        foreach (var b in value.FileBytes)
            hash.Add(b);
        hash.Add(value.FileName, StringComparer.Ordinal);
        hash.Add(value.FilePath, StringComparer.Ordinal);
        hash.Add(value.IsEnabled);
        return hash.ToHashCode();
    }

    private static int MultipartBodyEntryHash(MultipartBodyEntry value)
    {
        var hash = new HashCode();
        hash.Add(value.Key, StringComparer.Ordinal);
        hash.Add(value.IsFile);
        hash.Add(value.TextValue, StringComparer.Ordinal);
        hash.Add(value.FileName, StringComparer.Ordinal);
        hash.Add(value.FilePath, StringComparer.Ordinal);
        hash.Add(value.IsEnabled);
        return hash.ToHashCode();
    }

    private static void AddSequenceHash<T>(HashCode hash, IReadOnlyList<T> values)
    {
        foreach (var value in values)
            hash.Add(value);
    }

    private static void AddSequenceHash<T>(HashCode hash, IReadOnlyList<T> values, Func<T, int> getHash)
    {
        foreach (var value in values)
            hash.Add(getHash(value));
    }
}