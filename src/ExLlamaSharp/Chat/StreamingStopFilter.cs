namespace ExLlamaSharp.Chat;

/// <summary>
/// Streaming-safe stop/leak filter: holds back the longest suffix that is still
/// a prefix of a marker so a split <c>&lt;|eot_id|&gt;</c> is never emitted.
/// </summary>
public sealed class StreamingStopFilter
{
    private readonly string[] _markers;
    private readonly int _maxMarker;
    private string _hold = "";

    public StreamingStopFilter(IReadOnlyList<string>? markers = null)
    {
        _markers = markers is { Count: > 0 }
            ? markers.ToArray()
            : ChatTemplate.DefaultStopStrings;
        _maxMarker = 0;
        foreach (var m in _markers)
        {
            if (m.Length > _maxMarker)
            {
                _maxMarker = m.Length;
            }
        }
    }

    public bool Stopped { get; private set; }

    public string Push(string? text)
    {
        if (Stopped || string.IsNullOrEmpty(text))
        {
            return "";
        }

        _hold += text;

        var cut = -1;
        foreach (var marker in _markers)
        {
            var i = _hold.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && (cut < 0 || i < cut))
            {
                cut = i;
            }
        }

        if (cut >= 0)
        {
            var emit = _hold[..cut];
            _hold = "";
            Stopped = true;
            return emit;
        }

        var holdLen = LongestMarkerPrefixSuffix(_hold);
        if (holdLen <= 0)
        {
            var emit = _hold;
            _hold = "";
            return emit;
        }

        var released = _hold[..^holdLen];
        _hold = _hold[^holdLen..];
        return released;
    }

    public string Flush()
    {
        if (Stopped)
        {
            _hold = "";
            return "";
        }

        var emit = _hold;
        _hold = "";
        return emit;
    }

    private int LongestMarkerPrefixSuffix(string buffer)
    {
        var max = Math.Min(_maxMarker, buffer.Length);
        for (var n = max; n >= 1; n--)
        {
            var suffix = buffer.AsSpan(buffer.Length - n);
            foreach (var marker in _markers)
            {
                if (marker.AsSpan().StartsWith(suffix, StringComparison.Ordinal))
                {
                    return n;
                }
            }
        }

        return 0;
    }
}
