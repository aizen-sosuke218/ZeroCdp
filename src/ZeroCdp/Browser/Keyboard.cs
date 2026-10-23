namespace ZeroCdp.Browser;

/// <summary>One key press with the fields Chrome needs to build a native keyboard event.</summary>
internal readonly struct KeyStroke
{
    public KeyStroke(string key, string? code, string? text, int virtualKeyCode, int modifiers)
    {
        Key = key;
        Code = code;
        Text = text;
        VirtualKeyCode = virtualKeyCode;
        Modifiers = modifiers;
    }

    public string Key { get; }

    public string? Code { get; }

    public string? Text { get; }

    public int VirtualKeyCode { get; }

    public int Modifiers { get; }
}

/// <summary>
/// Turns Playwright-style key names into CDP keyboard fields.
/// </summary>
/// <remarks>
/// Only the keys automation actually needs are mapped. Unknown multi-character names are rejected
/// at the call site instead of being typed as a literal string.
/// </remarks>
internal static class KeyNames
{
    public const int Alt = 1;
    public const int Control = 2;
    public const int Meta = 4;
    public const int Shift = 8;

    public static KeyStroke Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(spec);

        if (spec == "+")
        {
            return ForCharacter('+', shift: false, modifiers: 0);
        }

        string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"'{spec}' is not a key name.", nameof(spec));
        }

        int modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= Modifier(parts[i]);
        }

        string keyName = parts[^1];
        int explicitModifier = TryModifier(keyName, out int modifier);
        if (explicitModifier != 0)
        {
            return new KeyStroke(
                ModifierKeyName(modifier),
                ModifierKeyName(modifier),
                null,
                ModifierVirtualKey(modifier),
                modifiers | modifier);
        }

        if (keyName.Length == 1)
        {
            char c = keyName[0];
            return ForCharacter(c, (modifiers & Shift) != 0, modifiers);
        }

        return Named(keyName, modifiers);
    }

    /// <summary>Builds a stroke for one character typed without modifiers.</summary>
    public static KeyStroke ForCharacter(char c, bool shift, int modifiers)
    {
        string text = c.ToString();
        string key = shift ? text.ToUpperInvariant() : text;
        if ((modifiers & (Control | Alt | Meta)) != 0 && (modifiers & Shift) == 0 && char.IsAsciiLetter(c))
        {
            key = char.ToLowerInvariant(c).ToString();
        }
        string? code;

        if (char.IsAsciiLetter(c))
        {
            code = $"Key{char.ToUpperInvariant(c)}";
        }
        else if (char.IsAsciiDigit(c))
        {
            code = $"Digit{c}";
        }
        else
        {
            code = c switch
            {
                ' ' => "Space",
                '-' => "Minus",
                '=' => "Equal",
                ';' => "Semicolon",
                '\'' => "Quote",
                ',' => "Comma",
                '.' => "Period",
                '/' => "Slash",
                '\\' => "Backslash",
                '[' => "BracketLeft",
                ']' => "BracketRight",
                '`' => "Backquote",
                _ => null,
            };
        }

        int virtualKey = char.IsAsciiLetter(c)
            ? char.ToUpperInvariant(c)
            : char.IsAsciiDigit(c)
                ? c
                : PunctuationVirtualKey(c);

        if ((modifiers & (Control | Alt | Meta)) != 0)
        {
            text = string.Empty;
        }

        return new KeyStroke(key, code, text.Length == 0 ? null : text, virtualKey, modifiers);
    }

    private static KeyStroke Named(string name, int modifiers)
    {
        return name.ToLowerInvariant() switch
        {
            "enter" or "return" => new KeyStroke("Enter", "Enter", NoModifierText(modifiers, "\r"), 13, modifiers),
            "tab" => new KeyStroke("Tab", "Tab", null, 9, modifiers),
            "escape" or "esc" => new KeyStroke("Escape", "Escape", null, 27, modifiers),
            "backspace" => new KeyStroke("Backspace", "Backspace", null, 8, modifiers),
            "delete" or "del" => new KeyStroke("Delete", "Delete", null, 46, modifiers),
            "arrowleft" or "left" => new KeyStroke("ArrowLeft", "ArrowLeft", null, 37, modifiers),
            "arrowup" or "up" => new KeyStroke("ArrowUp", "ArrowUp", null, 38, modifiers),
            "arrowright" or "right" => new KeyStroke("ArrowRight", "ArrowRight", null, 39, modifiers),
            "arrowdown" or "down" => new KeyStroke("ArrowDown", "ArrowDown", null, 40, modifiers),
            "home" => new KeyStroke("Home", "Home", null, 36, modifiers),
            "end" => new KeyStroke("End", "End", null, 35, modifiers),
            "pageup" => new KeyStroke("PageUp", "PageUp", null, 33, modifiers),
            "pagedown" => new KeyStroke("PageDown", "PageDown", null, 34, modifiers),
            "insert" => new KeyStroke("Insert", "Insert", null, 45, modifiers),
            "space" => new KeyStroke(" ", "Space", NoModifierText(modifiers, " "), 32, modifiers),
            _ when TryFunctionKey(name, out int functionKey) =>
                new KeyStroke(name.ToUpperInvariant(), name.ToUpperInvariant(), null, functionKey, modifiers),
            _ => throw new ArgumentException($"Unknown key name '{name}'.", nameof(name)),
        };
    }

    private static string? NoModifierText(int modifiers, string text)
        => (modifiers & (Control | Alt | Meta)) != 0 ? null : text;

    private static bool TryFunctionKey(string name, out int virtualKey)
    {
        virtualKey = 0;
        if (name.Length < 2 || (name[0] != 'F' && name[0] != 'f'))
        {
            return false;
        }

        if (!int.TryParse(name.AsSpan(1), out int number) || number is < 1 or > 12)
        {
            return false;
        }

        virtualKey = 111 + number;
        return true;
    }

    private static int Modifier(string name)
    {
        if (TryModifier(name, out int modifier) == 0)
        {
            throw new ArgumentException($"Unknown modifier '{name}'.");
        }

        return modifier;
    }

    private static int TryModifier(string name, out int modifier)
    {
        switch (name.ToLowerInvariant())
        {
            case "alt":
                modifier = Alt;
                return Alt;
            case "control":
            case "ctrl":
                modifier = Control;
                return Control;
            case "meta":
            case "command":
            case "cmd":
                modifier = Meta;
                return Meta;
            case "shift":
                modifier = Shift;
                return Shift;
            default:
                modifier = 0;
                return 0;
        }
    }

    private static string ModifierKeyName(int modifier) => modifier switch
    {
        Alt => "Alt",
        Control => "Control",
        Meta => "Meta",
        _ => "Shift",
    };

    private static int ModifierVirtualKey(int modifier) => modifier switch
    {
        Alt => 18,
        Control => 17,
        Meta => 91,
        _ => 16,
    };

    private static int PunctuationVirtualKey(char c) => c switch
    {
        ' ' => 32,
        ';' => 186,
        '=' => 187,
        ',' => 188,
        '-' => 189,
        '.' => 190,
        '/' => 191,
        '`' => 192,
        '[' => 219,
        '\\' => 220,
        ']' => 221,
        '\'' => 222,
        _ => 0,
    };
}
