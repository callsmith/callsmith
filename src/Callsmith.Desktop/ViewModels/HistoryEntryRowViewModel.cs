using System.Net;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Wraps a <see cref="HistoryEntry"/> for display in the history list.
/// </summary>
public sealed partial class HistoryEntryRowViewModel : ObservableObject
{
    public HistoryEntry Entry { get; }

    public string Method => Entry.Method;
    public int? StatusCode => Entry.StatusCode;
    public string? DetailStatusCode => ((HttpStatusCode?)Entry.StatusCode)?.ToString(); 
    public string DisplayUrl => Entry.ResolvedUrl;
    public string? RequestName => Entry.RequestName;
    public string? CollectionName => Entry.CollectionName;
    public string? EnvironmentName => Entry.EnvironmentName;
    public string? EnvironmentColor => Entry.EnvironmentColor;
    public long ElapsedMs => Entry.ElapsedMs;
    public string SentAtDisplay => FormatRelative(Entry.SentAt);
    public string DetailSentAtDisplay => Entry.SentAt.LocalDateTime.ToString("F");
    public string ElapsedDisplay => Entry.ElapsedMs < 1000
        ? $"{Entry.ElapsedMs} ms"
        : $"{Entry.ElapsedMs / 1000.0:F1} s";

    public string MethodColor => HttpMethodColors.Hex(Entry.Method);

    public string MethodPillLabel => Entry.Method switch
    {
        "DELETE"  => "DEL",
        "OPTIONS" => "OPT",
        "PATCH"   => "PTCH",
        var m     => m,
    };

    public string StatusColor => Entry.StatusCode switch
    {
        >= 200 and < 300 => "#4ec9b0",
        >= 300 and < 400 => "#dcdcaa",
        >= 400 and < 500 => "#ce9178",
        >= 500             => "#f48771",
        _                  => "#888888",
    };

    public HistoryEntryRowViewModel(HistoryEntry entry)
    {
        Entry = entry;
    }

    private static string FormatRelative(DateTimeOffset dt)
    {
        var delta = DateTimeOffset.UtcNow - dt;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return dt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }
}
