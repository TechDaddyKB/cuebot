namespace Cuebot.Integrations.Steam;

/// <summary>
/// Steam's text KeyValues format (registry.vdf, libraryfolders.vdf, appmanifest_*.acf): quoted keys, each followed
/// by a quoted value or a braced block. Keys are matched without regard to case, as Steam does. Comments ("//") and
/// conditionals ("[$WIN32]") are skipped. Malformed input yields what could be read, never an exception.
/// </summary>
public sealed class SteamVdf
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SteamVdf> _blocks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => _values;
    public IReadOnlyDictionary<string, SteamVdf> Blocks => _blocks;

    /// <summary>A value, or "" when missing.</summary>
    public string this[string key] => _values.GetValueOrDefault(key, "");

    /// <summary>A block, or null when missing.</summary>
    public SteamVdf? Block(string key) => _blocks.GetValueOrDefault(key);

    /// <summary>Follows a path of block names ("Registry", "HKCU", "Software", ...), or null when any is missing.</summary>
    public SteamVdf? Path(params string[] keys)
    {
        var at = this;
        foreach (var key in keys)
        {
            at = at.Block(key);
            if (at is null) return null;
        }
        return at;
    }

    public static SteamVdf Parse(string text)
    {
        var root = new SteamVdf();
        var pos = 0;
        ReadBlock(text, ref pos, root, topLevel: true);
        return root;
    }

    /// <summary>The file parsed, or null when it is missing or cannot be read.</summary>
    public static SteamVdf? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ReadBlock(string text, ref int pos, SteamVdf into, bool topLevel)
    {
        while (true)
        {
            var key = Next(text, ref pos);
            if (key is null) return;
            if (key is { Quoted: false, Text: "}" })
            {
                if (!topLevel) return;
                continue;
            }
            if (key is { Quoted: false, Text: "{" }) continue;   // a stray brace
            var value = Next(text, ref pos);
            if (value is null) return;
            if (value is { Quoted: false, Text: "{" })
            {
                var child = new SteamVdf();
                ReadBlock(text, ref pos, child, topLevel: false);
                into._blocks[key.Value.Text] = child;
            }
            else if (value is { Quoted: false, Text: "}" })
            {
                if (!topLevel) return;
            }
            else
            {
                into._values[key.Value.Text] = value.Value.Text;
            }
        }
    }

    private static (string Text, bool Quoted)? Next(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c == '[')   // a platform conditional such as [$WIN32]
            {
                while (pos < text.Length && text[pos] != ']') pos++;
                pos++;
                continue;
            }
            if (c is '{' or '}')
            {
                pos++;
                return (c.ToString(), false);
            }
            if (c == '"')
            {
                pos++;
                var sb = new System.Text.StringBuilder();
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        pos++;
                        sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', _ => text[pos] });
                    }
                    else sb.Append(text[pos]);
                    pos++;
                }
                pos++;
                return (sb.ToString(), true);
            }
            var start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) pos++;
            return (text[start..pos], true);
        }
        return null;
    }
}
