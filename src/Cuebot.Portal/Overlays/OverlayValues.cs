using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Portal.Overlays;

/// <summary>
/// Checks for the values mods put into overlays and widgets. Widget code is never theirs to change, but widgets paste
/// field values into their HTML, CSS and JS ({field} placeholders) and often into innerHTML, so a value that could close
/// a quote, open a tag or be read as code would be code by another name. Text is kept to characters that cannot do that,
/// and a field the widget pastes where no characters are safe (bare in a script, an unquoted attribute, an on… handler)
/// is the streamer's alone.
/// </summary>
public static partial class OverlayValues
{
    public const int MaxText = 500;

    /// <summary>
    /// Text a mod typed, made safe to paste anywhere a widget might put it: straight quotes and backticks become curly
    /// ones; &lt; &gt; \ { } and character references (&amp; followed by # or a letter) are refused, as are script
    /// addresses. Null with <paramref name="problem"/> set when refused.
    /// </summary>
    public static string? CleanText(string value, bool multiline, out string? problem)
    {
        problem = null;
        if (value.Length > MaxText) { problem = $"Text can be at most {MaxText} characters."; return null; }
        var text = new StringBuilder(value.Length);
        var doubleOpen = true;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '<' or '>' or '\\' or '{' or '}':
                    problem = "Text from the portal cannot contain < > \\ { or }.";
                    return null;
                case '&' when i + 1 < value.Length && (value[i + 1] == '#' || char.IsAsciiLetter(value[i + 1])):
                    problem = "Put a space after & (it could otherwise be read as code).";
                    return null;
                case '\'' or '`':
                    text.Append('’');
                    break;
                case '"':
                    text.Append(doubleOpen ? '“' : '”');
                    doubleOpen = !doubleOpen;
                    break;
                case '\n' when multiline:
                    text.Append('\n');
                    break;
                case '\r' when multiline:
                    break;
                case var _ when char.IsControl(c):
                    problem = "Text cannot contain control characters" + (multiline ? "." : " or line breaks.");
                    return null;
                default:
                    text.Append(c);
                    break;
            }
        }
        var result = text.ToString();
        if (ScriptAddress().IsMatch(new string([.. result.Where(ch => !char.IsWhiteSpace(ch) && !char.IsControl(ch))])))
        {
            problem = "Text cannot start with a script address (javascript:, data: or vbscript:).";
            return null;
        }
        return result;
    }

    /// <summary>A CSS colour: #rgb to #rrggbbaa, rgb()/rgba()/hsl()/hsla() with numbers, or a colour name.</summary>
    public static bool IsColor(string value) => value.Length <= 60 && Color().IsMatch(value.Trim());

    /// <summary>A font name (Google Fonts and system fonts use letters, digits and spaces).</summary>
    public static bool IsFont(string value) => Font().IsMatch(value);

    /// <summary>A number from JSON (a number, or a string holding one), or null.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.GetValueKind() == JsonValueKind.Number) return double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return v.GetValueKind() == JsonValueKind.String && double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
               && double.IsFinite(d) ? d : null;
    }

    public static string? String(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    /// <summary>
    /// Why the widget's code makes <paramref name="key"/> unsafe for mods, or null when every {key} placeholder sits in
    /// text, a quoted attribute that is not an on… handler, CSS, a JS string or a comment. When unsure, it is unsafe.
    /// </summary>
    public static string? UnsafePlacement(WidgetCode code, string key)
    {
        var placeholder = Placeholder(key);
        if (JsUnsafe(code.Js, placeholder)) return "The widget's script uses it directly, so only the streamer can change it.";
        var html = code.Html;
        foreach (Match script in Script().Matches(html))
            if (JsUnsafe(script.Groups["body"].Value, placeholder)) return "The widget's script uses it directly, so only the streamer can change it.";
        foreach (Match m in placeholder.Matches(html))
        {
            if (Script().Matches(html).Any(s => m.Index >= s.Index && m.Index < s.Index + s.Length)) continue;
            if (AttributeUnsafe(html, m.Index)) return "The widget uses it inside an HTML attribute where text could run, so only the streamer can change it.";
        }
        return null;
    }

    private static Regex Placeholder(string key) =>
        new(@"\{\{\s*" + Regex.Escape(key) + @"\s*\}\}|\{" + Regex.Escape(key) + @"\}", RegexOptions.CultureInvariant);

    /// <summary>True when a placeholder in <paramref name="js"/> is outside every string and comment.</summary>
    internal static bool JsUnsafe(string js, Regex placeholder)
    {
        var matches = placeholder.Matches(js);
        if (matches.Count == 0) return false;
        var starts = matches.Select(m => m.Index).ToHashSet();
        var state = 'c';   // c code, ' " ` strings, / line comment, * block comment
        var depth = 0;     // braces open in code
        var templates = new Stack<int>();   // the depth at which each ${…} inside a template string returns to it
        for (var i = 0; i < js.Length; i++)
        {
            var ch = js[i];
            if (state == 'c' && starts.Contains(i)) return true;
            switch (state)
            {
                case 'c':
                    if (ch is '\'' or '"' or '`') state = ch;
                    else if (ch == '/' && i + 1 < js.Length && js[i + 1] == '/') { state = '/'; i++; }
                    else if (ch == '/' && i + 1 < js.Length && js[i + 1] == '*') { state = '*'; i++; }
                    else if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        if (templates.Count > 0 && templates.Peek() == depth) { templates.Pop(); state = '`'; }
                        else depth--;
                    }
                    break;
                case '/':
                    if (ch == '\n') state = 'c';
                    break;
                case '*':
                    if (ch == '*' && i + 1 < js.Length && js[i + 1] == '/') { state = 'c'; i++; }
                    break;
                case '`':
                    if (ch == '\\') i++;
                    else if (ch == '`') state = 'c';
                    else if (ch == '$' && i + 1 < js.Length && js[i + 1] == '{' && !starts.Contains(i + 1)) { templates.Push(depth); state = 'c'; i++; }
                    break;
                default:
                    if (ch == '\\') i++;
                    else if (ch == state) state = 'c';
                    else if (ch == '\n') return true;   // an unclosed string: the scan is lost, so unsafe
                    break;
            }
        }
        return false;
    }

    /// <summary>True when the HTML position is inside a tag, unquoted or in an on… attribute (or cannot be told).</summary>
    private static bool AttributeUnsafe(string html, int index)
    {
        var open = html.LastIndexOf('<', Math.Max(0, index - 1));
        var close = html.LastIndexOf('>', Math.Max(0, index - 1));
        if (open < 0 || close > open) return false;   // in text
        var tag = html[open..index];
        // Walk the tag's attributes up to the placeholder, tracking quotes.
        char quote = '\0';
        var attribute = new StringBuilder();
        var name = "";
        var afterEquals = false;
        foreach (var ch in tag[1..])
        {
            if (quote != '\0')
            {
                if (ch == quote) { quote = '\0'; name = ""; afterEquals = false; }
                continue;
            }
            if (ch is '"' or '\'' && afterEquals) { quote = ch; continue; }
            if (ch == '=') { name = attribute.ToString(); attribute.Clear(); afterEquals = true; continue; }
            if (char.IsWhiteSpace(ch)) { if (!afterEquals) attribute.Clear(); continue; }
            if (afterEquals) { afterEquals = false; name = ""; }   // an unquoted value started
            attribute.Append(ch);
        }
        if (quote == '\0') return true;   // unquoted, or between attributes: unsafe
        return name.Trim().StartsWith("on", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(javascript|vbscript|data):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptAddress();

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|(rgb|rgba|hsl|hsla)\(\s*[0-9.,%\s/+-]*(deg)?[0-9.,%\s/+-]*\)|[a-zA-Z]{3,24})$", RegexOptions.CultureInvariant)]
    private static partial Regex Color();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex Font();

    [GeneratedRegex(@"<script\b[^>]*>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Script();
}
