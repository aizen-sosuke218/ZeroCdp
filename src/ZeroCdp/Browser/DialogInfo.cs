using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>The kind of JavaScript dialog a page opened.</summary>
public enum DialogType
{
    Alert = 0,
    Confirm = 1,
    Prompt = 2,
    BeforeUnload = 3,
}

/// <summary>Describes a JavaScript dialog that is currently blocking a page.</summary>
public readonly struct DialogInfo
{
    public DialogInfo(
        DialogType type,
        string message,
        string? defaultPrompt,
        string url,
        string? frameId)
    {
        Type = type;
        Message = message;
        DefaultPrompt = defaultPrompt;
        Url = url;
        FrameId = frameId;
    }

    public DialogType Type { get; }

    public string Message { get; }

    /// <summary>Default value of a prompt, or null for other dialog types.</summary>
    public string? DefaultPrompt { get; }

    public string Url { get; }

    public string? FrameId { get; }

    internal static bool TryParse(ReadOnlySpan<byte> paramsUtf8, out DialogInfo value)
    {
        value = default;
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        DialogType type = DialogType.Alert;
        string message = string.Empty;
        string? defaultPrompt = null;
        string url = string.Empty;
        string? frameId = null;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("type"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawType, out _))
            {
                type = ParseType(rawType);
            }
            else if (name.SequenceEqual("message"u8))
            {
                message = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("defaultPrompt"u8))
            {
                defaultPrompt = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("url"u8))
            {
                url = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("frameId"u8))
            {
                frameId = ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new DialogInfo(type, message, defaultPrompt, url, frameId);
        return true;
    }

    private static DialogType ParseType(ReadOnlySpan<byte> raw)
    {
        if (raw.SequenceEqual("confirm"u8))
        {
            return DialogType.Confirm;
        }

        if (raw.SequenceEqual("prompt"u8))
        {
            return DialogType.Prompt;
        }

        if (raw.SequenceEqual("beforeunload"u8))
        {
            return DialogType.BeforeUnload;
        }

        return DialogType.Alert;
    }

    private static string ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return string.Empty;
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }
}
