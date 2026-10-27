using System.Globalization;

namespace Cuebot.Core.Variables;

internal sealed class TextFunctionException(string message) : Exception(message);

internal delegate string TextFunctionBody(string[] args, in ResolveScope scope);

/// <summary>
/// An inline function usable in any text field as <c>$name(arguments)$</c>. Single-argument functions take
/// everything between the parentheses (commas included); multi-argument ones split on top-level commas.
/// </summary>
internal sealed record TextFunction(string Name, int MinArgs, int MaxArgs, bool MultiArgument, string Usage, TextFunctionBody Invoke);

/// <summary>
/// The inline functions. Evaluation order: variables inside a call are substituted first, then calls are
/// evaluated innermost first. A call that fails is left in the text (with its variables substituted) and the
/// problem is logged once. Escapes: <c>\$name(</c> writes a literal call; inside arguments <c>\,</c> <c>\(</c>
/// <c>\)</c> write literal characters.
/// </summary>
public static class TextFunctions
{
    private static readonly Dictionary<string, TextFunction> Functions = Build().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Usage lines for help text and editors, e.g. "$math(expression)$".</summary>
    public static IReadOnlyList<string> Usages { get; } = Functions.Values.Select(f => f.Usage).OrderBy(u => u, StringComparer.Ordinal).ToArray();

    internal static TextFunction? Find(string name) => Functions.GetValueOrDefault(name);

    private static IEnumerable<TextFunction> Build()
    {
        yield return new("math", 1, 1, false, "$math(expression)$", static (a, in s) =>
        {
            try
            {
                return MathEvaluator.FormatNumber(MathEvaluator.Evaluate(a[0], s.Env.Random));
            }
            catch (MathException ex)
            {
                throw new TextFunctionException(ex.Message);
            }
        });
        yield return new("length", 1, 1, false, "$length(text)$", static (a, in _) =>
            new StringInfo(a[0]).LengthInTextElements.ToString(CultureInfo.InvariantCulture));
        yield return new("upper", 1, 1, false, "$upper(text)$", static (a, in _) => a[0].ToUpperInvariant());
        yield return new("lower", 1, 1, false, "$lower(text)$", static (a, in _) => a[0].ToLowerInvariant());
        yield return new("trim", 1, 1, false, "$trim(text)$", static (a, in _) => a[0].Trim());
        yield return new("urlencode", 1, 1, false, "$urlencode(text)$", static (a, in _) => Uri.EscapeDataString(a[0]));
        yield return new("replace", 3, 3, true, "$replace(text,find,replacement)$", static (a, in _) =>
        {
            if (a[1].Length == 0) throw new TextFunctionException("the text to find is empty");
            return a[0].Replace(a[1], a[2], StringComparison.Ordinal);
        });
        yield return new("substring", 2, 3, true, "$substring(text,start[,length])$", static (a, in _) => Substring(a));
        yield return new("pick", 1, int.MaxValue, true, "$pick(a,b,c...)$", static (a, in s) => a[s.Env.Random.Next(a.Length)]);
        yield return new("parse", 1, 1, false, "$parse(variableName)$", static (a, in s) => Parse(a[0], s));
    }

    /// <summary>Text elements (so emoji count once). Negative start counts from the end; ranges are clamped.</summary>
    private static string Substring(string[] a)
    {
        var info = new StringInfo(a[0]);
        var total = info.LengthInTextElements;
        var start = Integer(a[1], "start");
        if (start < 0) start = Math.Max(0, total + start);
        if (start >= total) return "";
        var length = a.Length > 2 && a[2].Trim().Length > 0 ? Integer(a[2], "length") : total - start;
        if (length < 0) throw new TextFunctionException("length is negative");
        length = Math.Min(length, total - start);
        return length == 0 ? "" : info.SubstringByTextElements(start, length);
    }

    private static int Integer(string text, string what) =>
        int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new TextFunctionException($"{what} '{text}' is not a whole number");

    /// <summary>
    /// Resolves a variable whose name was built from other variables: <c>$parse(line%i%)$</c> → value of %line3%.
    /// A bare name is looked up as an argument/built-in, then as a global; "%name%" or "~name~" pick one; a
    /// ":format" suffix is allowed. The value is inserted as-is (it is not parsed again).
    /// </summary>
    private static string Parse(string text, in ResolveScope scope)
    {
        var name = text.Trim();
        char? sigil = null;
        if (name.Length >= 2 && name[0] is '%' or '~' && name[^1] == name[0])
        {
            sigil = name[0];
            name = name[1..^1];
        }
        string? format = null;
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            format = name[(colon + 1)..];
            name = name[..colon];
        }
        if (name.Length == 0 || !name.All(TextTemplate.IsNameChar)) throw new TextFunctionException($"'{text}' is not a variable name");

        if (sigil != '~' && TemplateRenderer.TryResolve(false, name, format, null, scope, out var result)) return result;
        if (sigil != '%' && TemplateRenderer.TryResolve(true, name, format, null, scope, out result)) return result;
        throw new TextFunctionException($"there is no variable named '{name}'");
    }
}
