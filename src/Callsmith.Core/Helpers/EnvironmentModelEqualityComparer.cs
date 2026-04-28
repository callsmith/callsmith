using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Strict value comparer for persisted environment editor state.
/// </summary>
public sealed class EnvironmentModelEqualityComparer : IEqualityComparer<EnvironmentModel>
{
    public static EnvironmentModelEqualityComparer Instance { get; } = new();

    public bool Equals(EnvironmentModel? x, EnvironmentModel? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;

        return string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal)
            && x.EnvironmentId == y.EnvironmentId
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal)
            && string.Equals(x.Color, y.Color, StringComparison.Ordinal)
            && string.Equals(x.GlobalPreviewEnvironmentName, y.GlobalPreviewEnvironmentName, StringComparison.Ordinal)
            && EqualSequence(x.Variables, y.Variables, VariableEquals);
    }

    public int GetHashCode(EnvironmentModel obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var hash = new HashCode();
        hash.Add(obj.FilePath, StringComparer.Ordinal);
        hash.Add(obj.EnvironmentId);
        hash.Add(obj.Name, StringComparer.Ordinal);
        hash.Add(obj.Color, StringComparer.Ordinal);
        hash.Add(obj.GlobalPreviewEnvironmentName, StringComparer.Ordinal);
        foreach (var variable in obj.Variables)
            hash.Add(VariableHash(variable));
        return hash.ToHashCode();
    }

    private static bool VariableEquals(EnvironmentVariable x, EnvironmentVariable y) =>
        string.Equals(x.Name, y.Name, StringComparison.Ordinal)
        && string.Equals(x.Value, y.Value, StringComparison.Ordinal)
        && string.Equals(x.VariableType, y.VariableType, StringComparison.Ordinal)
        && x.IsSecret == y.IsSecret
        && x.IsForceGlobalOverride == y.IsForceGlobalOverride
        && string.Equals(x.MockDataCategory, y.MockDataCategory, StringComparison.Ordinal)
        && string.Equals(x.MockDataField, y.MockDataField, StringComparison.Ordinal)
        && string.Equals(x.ResponseRequestName, y.ResponseRequestName, StringComparison.Ordinal)
        && string.Equals(x.ResponsePath, y.ResponsePath, StringComparison.Ordinal)
        && x.ResponseMatcher == y.ResponseMatcher
        && x.ResponseFrequency == y.ResponseFrequency
        && x.ResponseExpiresAfterSeconds == y.ResponseExpiresAfterSeconds
        && EqualSegments(x.Segments, y.Segments);

    private static bool EqualSegments(IReadOnlyList<ValueSegment>? x, IReadOnlyList<ValueSegment>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return EqualSequence(x, y, SegmentEquals);
    }

    private static bool SegmentEquals(ValueSegment x, ValueSegment y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x.GetType() != y.GetType()) return false;

        return x switch
        {
            StaticValueSegment sx => y is StaticValueSegment sy
                && string.Equals(sx.Text, sy.Text, StringComparison.Ordinal),
            DynamicValueSegment dx => y is DynamicValueSegment dy
                && string.Equals(dx.RequestName, dy.RequestName, StringComparison.Ordinal)
                && string.Equals(dx.Path, dy.Path, StringComparison.Ordinal)
                && dx.Matcher == dy.Matcher
                && dx.Frequency == dy.Frequency
                && dx.ExpiresAfterSeconds == dy.ExpiresAfterSeconds,
            MockDataSegment mx => y is MockDataSegment my
                && string.Equals(mx.Category, my.Category, StringComparison.Ordinal)
                && string.Equals(mx.Field, my.Field, StringComparison.Ordinal),
            _ => false,
        };
    }

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

    private static int VariableHash(EnvironmentVariable value)
    {
        var hash = new HashCode();
        hash.Add(value.Name, StringComparer.Ordinal);
        hash.Add(value.Value, StringComparer.Ordinal);
        hash.Add(value.VariableType, StringComparer.Ordinal);
        hash.Add(value.IsSecret);
        hash.Add(value.IsForceGlobalOverride);
        hash.Add(value.MockDataCategory, StringComparer.Ordinal);
        hash.Add(value.MockDataField, StringComparer.Ordinal);
        hash.Add(value.ResponseRequestName, StringComparer.Ordinal);
        hash.Add(value.ResponsePath, StringComparer.Ordinal);
        hash.Add(value.ResponseMatcher);
        hash.Add(value.ResponseFrequency);
        hash.Add(value.ResponseExpiresAfterSeconds);
        if (value.Segments is not null)
        {
            foreach (var segment in value.Segments)
                hash.Add(SegmentHash(segment));
        }
        return hash.ToHashCode();
    }

    private static int SegmentHash(ValueSegment segment)
    {
        var hash = new HashCode();
        switch (segment)
        {
            case StaticValueSegment s:
                hash.Add("static", StringComparer.Ordinal);
                hash.Add(s.Text, StringComparer.Ordinal);
                break;
            case DynamicValueSegment d:
                hash.Add("dynamic", StringComparer.Ordinal);
                hash.Add(d.RequestName, StringComparer.Ordinal);
                hash.Add(d.Path, StringComparer.Ordinal);
                hash.Add(d.Matcher);
                hash.Add(d.Frequency);
                hash.Add(d.ExpiresAfterSeconds);
                break;
            case MockDataSegment m:
                hash.Add("mock", StringComparer.Ordinal);
                hash.Add(m.Category, StringComparer.Ordinal);
                hash.Add(m.Field, StringComparer.Ordinal);
                break;
        }
        return hash.ToHashCode();
    }
}