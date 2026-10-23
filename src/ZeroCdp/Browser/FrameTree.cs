using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>One frame as reported by <c>Page.getFrameTree</c> or <c>Page.frameNavigated</c>.</summary>
internal readonly struct FrameTreeEntry
{
    public string FrameId { get; init; }

    public string? ParentFrameId { get; init; }

    public string Name { get; init; }

    public string Url { get; init; }

    public string LoaderId { get; init; }

    public string SecurityOrigin { get; init; }

    public string MimeType { get; init; }

    public static bool TryParse(
        ref JsonCursor cursor,
        string? inheritedParentFrameId,
        out FrameTreeEntry entry)
    {
        entry = default;
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string frameId = string.Empty;
        string? parentFrameId = inheritedParentFrameId;
        string name = string.Empty;
        string url = string.Empty;
        string loaderId = string.Empty;
        string securityOrigin = string.Empty;
        string mimeType = string.Empty;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("id"u8))
            {
                if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    return false;
                }

                frameId = Encoding.UTF8.GetString(raw);
            }
            else if (property.SequenceEqual("parentId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    parentFrameId = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("name"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    name = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("url"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    url = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("loaderId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    loaderId = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("securityOrigin"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    securityOrigin = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("mimeType"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    mimeType = Encoding.UTF8.GetString(raw);
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (frameId.Length == 0)
        {
            return false;
        }

        entry = new FrameTreeEntry
        {
            FrameId = frameId,
            ParentFrameId = string.IsNullOrEmpty(parentFrameId) ? null : parentFrameId,
            Name = name,
            Url = url,
            LoaderId = loaderId,
            SecurityOrigin = securityOrigin,
            MimeType = mimeType,
        };
        return true;
    }
}

/// <summary>Parses the complete nested frame tree returned by <c>Page.getFrameTree</c>.</summary>
internal static class FrameTreeParser
{
    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out List<FrameTreeEntry> frames)
    {
        frames = new List<FrameTreeEntry>();
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("frameTree"u8))
        {
            return false;
        }

        return TryParseTree(ref cursor, inheritedParentFrameId: null, frames);
    }

    private static bool TryParseTree(
        ref JsonCursor cursor,
        string? inheritedParentFrameId,
        List<FrameTreeEntry> frames)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        FrameTreeEntry frame = default;
        bool hasFrame = false;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("frame"u8))
            {
                if (!FrameTreeEntry.TryParse(ref cursor, inheritedParentFrameId, out frame))
                {
                    return false;
                }

                hasFrame = true;
                frames.Add(frame);
            }
            else if (property.SequenceEqual("childFrames"u8))
            {
                if (!cursor.TryEnterArray())
                {
                    return false;
                }

                string? childParent = hasFrame ? frame.FrameId : inheritedParentFrameId;
                while (cursor.TryNextArrayElement())
                {
                    if (!TryParseTree(ref cursor, childParent, frames))
                    {
                        return false;
                    }
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return hasFrame;
    }
}
