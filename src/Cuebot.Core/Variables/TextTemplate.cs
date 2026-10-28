using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Variables;

internal abstract class TemplateNode;

internal sealed class LiteralNode(string text) : TemplateNode
{
    public string Text { get; } = text;
}

/// <summary><c>%name%</c>, <c>%name:format%</c>, <c>~name~</c> or <c>~name:format~</c> (format may end in "|culture").</summary>
internal sealed class VariableNode(bool global, string name, string? format, string? culture, string raw) : TemplateNode
{
    public bool Global { get; } = global;
    public string Name { get; } = name;
    public string? Format { get; } = format;
    public string? Culture { get; } = culture;
    public string Raw { get; } = raw;
}

internal sealed class FunctionNode(TextFunction function, TemplateNode[][] args) : TemplateNode
{
    public TextFunction Function { get; } = function;
    public TemplateNode[][] Args { get; } = args;
}

/// <summary>
/// A parsed text template. The structure (variables and function calls) comes from the template only: values
/// substituted into it are never parsed again, so a chat message containing "%x%" or "$math(...)$" stays literal
/// text. <c>$parse(...)$</c> is the explicit way to look up a variable whose name is built at run time.
/// </summary>
internal sealed class TextTemplate
{
    private const int CacheLimit = 4096;
    private static readonly ConcurrentDictionary<string, TextTemplate> Cache = new(StringComparer.Ordinal);

    private TextTemplate(TemplateNode[] nodes, string? literal)
    {
        Nodes = nodes;
        Literal = literal;
    }

    public TemplateNode[] Nodes { get; }

    /// <summary>Set when the template has no variables or functions (after escapes were applied).</summary>
    public string? Literal { get; }

    public static TextTemplate Get(string template)
    {
        if (Cache.TryGetValue(template, out var cached)) return cached;
        var parsed = Parse(template);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[template] = parsed;
        return parsed;
    }

    internal static TextTemplate Parse(string template)
    {
        var nodes = new Parser(template).ParseTop();
        string? literal = nodes.Length switch
        {
            0 => "",
            1 when nodes[0] is LiteralNode l => l.Text,
            _ => null,
        };
        return new TextTemplate(nodes, literal);
    }

    // '[' and ']' allow indexed names such as %match[1]% and %fw.variants[0].id%.
    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '[' or ']';

    private sealed class Parser(string s)
    {
        private const int MaxFormatLength = 100;
        private int _pos;

        public TemplateNode[] ParseTop()
        {
            var nodes = new List<TemplateNode>();
            var text = new StringBuilder();
            while (_pos < s.Length)
            {
                if (!TrySpecial(nodes, text)) text.Append(s[_pos++]);
            }
            Flush(nodes, text);
            return [.. nodes];
        }

        /// <summary>Parses function arguments after "(" up to the closing ")$". Returns null when there is none.</summary>
        private TemplateNode[][]? ParseArgs(bool multiArgument)
        {
            var args = new List<TemplateNode[]>();
            var nodes = new List<TemplateNode>();
            var text = new StringBuilder();
            var depth = 0;
            while (_pos < s.Length)
            {
                var c = s[_pos];
                var next = _pos + 1 < s.Length ? s[_pos + 1] : '\0';
                if (c == '\\' && next is ',' or '(' or ')')
                {
                    text.Append(next);
                    _pos += 2;
                }
                else if (c == ')' && depth == 0 && next == '$')
                {
                    _pos += 2;
                    Flush(nodes, text);
                    args.Add([.. nodes]);
                    return [.. args];
                }
                else if (c == ')')
                {
                    if (depth > 0) depth--;
                    text.Append(c);
                    _pos++;
                }
                else if (c == '(')
                {
                    depth++;
                    text.Append(c);
                    _pos++;
                }
                else if (c == ',' && multiArgument && depth == 0)
                {
                    Flush(nodes, text);
                    args.Add([.. nodes]);
                    nodes.Clear();
                    _pos++;
                }
                else if (!TrySpecial(nodes, text))
                {
                    text.Append(c);
                    _pos++;
                }
            }
            return null;
        }

        private bool TrySpecial(List<TemplateNode> nodes, StringBuilder text)
        {
            var c = s[_pos];
            TemplateNode? node = null;
            if (c is '%' or '~')
            {
                node = TryVariable(c);
            }
            else if (c == '$')
            {
                node = TryFunction();
            }
            else if (c == '\\' && _pos + 1 < s.Length && s[_pos + 1] == '$' && FunctionAt(_pos + 1, out _, out _) is not null)
            {
                // "\$name(" writes a literal "$name(" instead of calling the function.
                text.Append('$');
                _pos += 2;
                return true;
            }
            if (node is null) return false;
            Flush(nodes, text);
            nodes.Add(node);
            return true;
        }

        private VariableNode? TryVariable(char sigil)
        {
            var j = _pos + 1;
            while (j < s.Length && IsNameChar(s[j])) j++;
            if (j == _pos + 1 || j >= s.Length) return null;
            var name = s.Substring(_pos + 1, j - _pos - 1);
            string? format = null;
            string? culture = null;
            int end;
            if (s[j] == sigil)
            {
                end = j + 1;
            }
            else if (s[j] == ':')
            {
                var k = j + 1;
                while (k < s.Length && s[k] != sigil && s[k] is not ('\n' or '\r') && k - j <= MaxFormatLength) k++;
                if (k >= s.Length || s[k] != sigil || k == j + 1) return null;
                format = s.Substring(j + 1, k - j - 1);
                var bar = format.LastIndexOf('|');
                if (bar > 0 && IsCultureName(format.AsSpan(bar + 1)))
                {
                    culture = format[(bar + 1)..];
                    format = format[..bar];
                }
                end = k + 1;
            }
            else
            {
                return null;
            }
            var raw = s.Substring(_pos, end - _pos);
            _pos = end;
            return new VariableNode(sigil == '~', name, format, culture, raw);
        }

        private FunctionNode? TryFunction()
        {
            var function = FunctionAt(_pos, out _, out var open);
            if (function is null) return null;
            var start = _pos;
            _pos = open + 1;
            var args = ParseArgs(function.MultiArgument);
            if (args is null)
            {
                _pos = start;
                return null;
            }
            return new FunctionNode(function, args);
        }

        /// <summary>Recognizes "$name(" for a known function at <paramref name="index"/>.</summary>
        private TextFunction? FunctionAt(int index, out string name, out int open)
        {
            name = "";
            open = -1;
            var j = index + 1;
            while (j < s.Length && char.IsAsciiLetterOrDigit(s[j])) j++;
            if (j == index + 1 || j >= s.Length || s[j] != '(') return null;
            name = s.Substring(index + 1, j - index - 1);
            open = j;
            return TextFunctions.Find(name);
        }

        private static bool IsCultureName(ReadOnlySpan<char> text)
        {
            if (text.Length < 2 || text.Length > 20 || !char.IsAsciiLetter(text[0]) || !char.IsAsciiLetter(text[1])) return false;
            foreach (var c in text)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
            }
            return true;
        }

        private static void Flush(List<TemplateNode> nodes, StringBuilder text)
        {
            if (text.Length == 0) return;
            nodes.Add(new LiteralNode(text.ToString()));
            text.Clear();
        }
    }
}

/// <summary>Everything a template needs at render time.</summary>
internal readonly struct ResolveScope(IReadOnlyDictionary<string, object?> args, Func<string, string?>? globals, TextEnvironment env)
{
    public IReadOnlyDictionary<string, object?> Args { get; } = args;
    public Func<string, string?>? Globals { get; } = globals;
    public TextEnvironment Env { get; } = env;
}

internal static class TemplateRenderer
{
    private static readonly ConcurrentDictionary<string, CultureInfo?> Cultures = new(StringComparer.OrdinalIgnoreCase);

    public static string Render(TextTemplate template, in ResolveScope scope)
    {
        if (template.Literal is not null) return template.Literal;
        var builder = new StringBuilder();
        Render(template.Nodes, builder, scope);
        return builder.ToString();
    }

    private static void Render(TemplateNode[] nodes, StringBuilder builder, in ResolveScope scope)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case LiteralNode literal:
                    builder.Append(literal.Text);
                    break;
                case VariableNode variable:
                    builder.Append(TryResolve(variable.Global, variable.Name, variable.Format, variable.Culture, scope, out var text)
                        ? text
                        : variable.Raw);
                    break;
                case FunctionNode function:
                    builder.Append(Call(function, scope));
                    break;
            }
        }
    }

    private static string Call(FunctionNode node, in ResolveScope scope)
    {
        var values = new string[node.Args.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var arg = node.Args[i];
            if (arg.Length == 1 && arg[0] is LiteralNode only)
            {
                values[i] = only.Text;
                continue;
            }
            var builder = new StringBuilder();
            Render(arg, builder, scope);
            values[i] = builder.ToString();
        }

        var function = node.Function;
        try
        {
            if (values.Length < function.MinArgs || values.Length > function.MaxArgs)
            {
                throw new TextFunctionException(function.MinArgs == function.MaxArgs
                    ? $"expects {function.MinArgs} argument(s), got {values.Length}"
                    : $"expects {function.MinArgs} to {function.MaxArgs} arguments, got {values.Length}");
            }
            return function.Invoke(values, scope);
        }
        catch (TextFunctionException ex)
        {
            var text = $"${function.Name}({string.Join(",", values)})$";
            scope.Env.ReportError($"{text} could not be evaluated: {ex.Message}");
            return text;
        }
    }

    /// <summary>Resolves one variable reference, applying its format. False leaves the reference as written.</summary>
    internal static bool TryResolve(bool global, string name, string? format, string? cultureName, in ResolveScope scope, out string text)
    {
        text = "";
        object? value;
        if (global)
        {
            if (scope.Globals?.Invoke(name) is not { } g) return false;
            value = g;
        }
        else if (!TryGetArg(scope.Args, name, out value) && !BuiltInVariables.TryGet(name, ref format, scope.Env, out value))
        {
            return false;
        }

        var culture = scope.Env.Culture;
        if (cultureName is not null)
        {
            var found = Cultures.GetOrAdd(cultureName, static n =>
            {
                try
                {
                    return CultureInfo.GetCultureInfo(n, predefinedOnly: true);
                }
                catch (CultureNotFoundException)
                {
                    return null;
                }
            });
            if (found is null)
            {
                scope.Env.ReportError($"Unknown culture '{cultureName}' in {(global ? '~' : '%')}{name}:{format}|{cultureName}{(global ? '~' : '%')}");
                return false;
            }
            culture = found;
        }

        if (ValueFormatter.TryFormat(value, format, culture, out text)) return true;
        scope.Env.ReportError($"Invalid format '{format}' for {(global ? '~' : '%')}{name}{(global ? '~' : '%')} (value '{VariableResolver.Format(value)}')");
        return false;
    }

    internal static bool TryGetArg(IReadOnlyDictionary<string, object?> args, string key, out object? value)
    {
        if (args.TryGetValue(key, out value)) return true;
        if (args is Dictionary<string, object?> d && ReferenceEquals(d.Comparer, StringComparer.OrdinalIgnoreCase)) return false;
        // Fall back to a case-insensitive scan for dictionaries created with the default comparer.
        foreach (var pair in args)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        return false;
    }
}
