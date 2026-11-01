using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicCat.CLVKM.Core.Ipc;

/// <summary>What the background watcher reports to the app that owns the keyboard.</summary>
public enum WatcherEventKind
{
    /// <summary>Ignored placeholder, so a corrupted message cannot be mistaken for a real one.</summary>
    Unknown = 0,

    /// <summary>A key went down: the user is typing.</summary>
    Keypress = 1,

    /// <summary>Windows put a toast notification on screen.</summary>
    Notification = 2
}

/// <summary>
/// One message travelling from the watcher to the tray app over the pipe. Deliberately
/// small: the pipe only says what happened, and the app decides what the keyboard does.
/// </summary>
public sealed record WatcherEvent(WatcherEventKind Kind, int Count = 1, string? Source = null)
{
    /// <summary>One line of JSON — the pipe frames messages by newline.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Format);

    /// <summary>Reads one line written by <see cref="ToJson"/>; null when it is not ours.</summary>
    public static WatcherEvent? FromJson(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<WatcherEvent>(line, Format);

            if (parsed?.Kind is not (WatcherEventKind.Keypress or WatcherEventKind.Notification))
            {
                return null;
            }

            return parsed.Count < 1 ? parsed with { Count = 1 } : parsed;
        }
        catch (JsonException)
        {
            // A truncated or foreign line is not worth a crash: the next one will be fine.
            return null;
        }
    }

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
