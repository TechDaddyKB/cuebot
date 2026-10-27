using System.Globalization;

namespace Cuebot.Core.Variables;

public sealed class MathException(string message) : Exception(message);

/// <summary>
/// A small, safe arithmetic evaluator for <c>$math(...)$</c> (no code execution, no reflection).
/// <para>Precedence, lowest first: <c>||</c>; <c>&amp;&amp;</c>; <c>== != = &lt;&gt;</c>; <c>&lt; &lt;= &gt; &gt;=</c>;
/// <c>+ -</c>; <c>* / %</c>; unary <c>- + !</c>; <c>^</c> (right-associative, binds tighter than unary minus,
/// so <c>-2^2</c> is -4 and <c>2^-1</c> is 0.5). Comparisons and logical operators return 1 or 0; any non-zero
/// value is true.</para>
/// <para>Constants: <c>pi</c>, <c>e</c>, <c>true</c>, <c>false</c>. Functions: <c>min max</c> (one or more
/// arguments), <c>abs round(x[,digits]) floor ceil trunc sign sqrt pow(x,y) exp ln log(x[,base]) log10
/// sin cos tan clamp(x,lo,hi) if(cond,a,b) random() random(a,b)</c>. <c>random(a,b)</c> returns an integer in
/// [a, b] when both bounds are integers, otherwise a real number in [a, b). <c>round</c> rounds halves away from
/// zero.</para>
/// <para>Errors (syntax, unknown names, division or modulo by zero, results that are not finite numbers) throw
/// <see cref="MathException"/>.</para>
/// </summary>
public static class MathEvaluator
{
    public const int MaxLength = 4096;
    private const int MaxDepth = 64;

    public static double Evaluate(string expression, Random? random = null)
    {
        if (expression.Length > MaxLength) throw new MathException($"expression is longer than {MaxLength} characters");
        var parser = new Parser(expression, random ?? Random.Shared);
        var value = parser.ParseAll();
        if (!double.IsFinite(value)) throw new MathException("result is not a finite number");
        return value;
    }

    public static bool TryEvaluate(string expression, out double value, out string? error, Random? random = null)
    {
        try
        {
            value = Evaluate(expression, random);
            error = null;
            return true;
        }
        catch (MathException ex)
        {
            value = 0;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Whole numbers print without decimals; others with up to 15 significant digits (0.1+0.2 → 0.3).</summary>
    public static string FormatNumber(double value)
    {
        if (value == 0) return "0"; // also turns -0 into 0
        if (Math.Abs(value) < 1e15 && value == Math.Truncate(value)) return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    private sealed class Parser(string text, Random random)
    {
        private int _pos;
        private int _depth;

        public double ParseAll()
        {
            SkipSpaces();
            if (_pos >= text.Length) throw new MathException("expression is empty");
            var value = ParseOr();
            SkipSpaces();
            if (_pos < text.Length) throw new MathException($"unexpected '{text[_pos]}' at position {_pos + 1}");
            return value;
        }

        private double ParseOr()
        {
            var left = ParseAnd();
            while (Match("||")) left = Truth(left) | Truth(ParseAnd()) ? 1 : 0;
            return left;
        }

        private double ParseAnd()
        {
            var left = ParseEquality();
            while (Match("&&")) left = Truth(left) & Truth(ParseEquality()) ? 1 : 0;
            return left;
        }

        private double ParseEquality()
        {
            var left = ParseComparison();
            while (true)
            {
                if (Match("==")) left = left == ParseComparison() ? 1 : 0;
                else if (Match("!=") || Match("<>")) left = left != ParseComparison() ? 1 : 0;
                else if (MatchSingle('=')) left = left == ParseComparison() ? 1 : 0;
                else return left;
            }
        }

        private double ParseComparison()
        {
            var left = ParseAdditive();
            while (true)
            {
                if (Match("<=")) left = left <= ParseAdditive() ? 1 : 0;
                else if (Match(">=")) left = left >= ParseAdditive() ? 1 : 0;
                else if (MatchSingle('<')) left = left < ParseAdditive() ? 1 : 0;
                else if (MatchSingle('>')) left = left > ParseAdditive() ? 1 : 0;
                else return left;
            }
        }

        private double ParseAdditive()
        {
            var left = ParseTerm();
            while (true)
            {
                if (MatchSingle('+')) left += ParseTerm();
                else if (MatchSingle('-')) left -= ParseTerm();
                else return left;
            }
        }

        private double ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                if (MatchSingle('*'))
                {
                    left *= ParseUnary();
                }
                else if (MatchSingle('/'))
                {
                    var right = ParseUnary();
                    if (right == 0) throw new MathException("division by zero");
                    left /= right;
                }
                else if (MatchSingle('%'))
                {
                    var right = ParseUnary();
                    if (right == 0) throw new MathException("modulo by zero");
                    left %= right;
                }
                else
                {
                    return left;
                }
            }
        }

        private double ParseUnary()
        {
            Enter();
            try
            {
                if (MatchSingle('-')) return -ParseUnary();
                if (MatchSingle('+')) return ParseUnary();
                if (MatchSingle('!')) return Truth(ParseUnary()) ? 0 : 1;
                return ParsePower();
            }
            finally
            {
                _depth--;
            }
        }

        private double ParsePower()
        {
            var value = ParsePrimary();
            if (MatchSingle('^')) value = Math.Pow(value, ParseUnary());
            return value;
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= text.Length) throw new MathException("unexpected end of expression");
            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                Enter();
                var value = ParseOr();
                _depth--;
                Expect(')');
                return value;
            }
            if (char.IsAsciiDigit(c) || c == '.') return ParseNumber();
            if (char.IsAsciiLetter(c) || c == '_') return ParseIdentifier();
            throw new MathException($"unexpected '{c}' at position {_pos + 1}");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.')) _pos++;
            if (_pos < text.Length && (text[_pos] == 'e' || text[_pos] == 'E'))
            {
                var mark = _pos++;
                if (_pos < text.Length && (text[_pos] == '+' || text[_pos] == '-')) _pos++;
                if (_pos < text.Length && char.IsAsciiDigit(text[_pos]))
                {
                    while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
                }
                else
                {
                    _pos = mark; // "2e" is the number 2 followed by the constant e: let the caller report it
                }
            }
            var token = text.AsSpan(start, _pos - start);
            if (!double.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value))
            {
                throw new MathException($"'{token}' is not a number");
            }
            return value;
        }

        private double ParseIdentifier()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            var name = text.Substring(start, _pos - start).ToLowerInvariant();
            SkipSpaces();
            if (_pos < text.Length && text[_pos] == '(')
            {
                _pos++;
                Enter();
                var args = new List<double>();
                SkipSpaces();
                if (_pos < text.Length && text[_pos] == ')')
                {
                    _pos++;
                }
                else
                {
                    do args.Add(ParseOr());
                    while (MatchSingle(','));
                    Expect(')');
                }
                _depth--;
                return Call(name, args);
            }
            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                "true" => 1,
                "false" => 0,
                _ => throw new MathException($"unknown name '{name}'"),
            };
        }

        private double Call(string name, List<double> a)
        {
            switch (name)
            {
                case "min": Arity(name, a, 1, int.MaxValue); return a.Min();
                case "max": Arity(name, a, 1, int.MaxValue); return a.Max();
                case "abs": Arity(name, a, 1); return Math.Abs(a[0]);
                case "floor": Arity(name, a, 1); return Math.Floor(a[0]);
                case "ceil": Arity(name, a, 1); return Math.Ceiling(a[0]);
                case "trunc": Arity(name, a, 1); return Math.Truncate(a[0]);
                case "sign": Arity(name, a, 1); return Math.Sign(a[0]);
                case "sqrt":
                    Arity(name, a, 1);
                    if (a[0] < 0) throw new MathException("sqrt of a negative number");
                    return Math.Sqrt(a[0]);
                case "pow": Arity(name, a, 2); return Math.Pow(a[0], a[1]);
                case "exp": Arity(name, a, 1); return Math.Exp(a[0]);
                case "ln": Arity(name, a, 1); return Log(a[0]);
                case "log10": Arity(name, a, 1); return Log(a[0]) / Math.Log(10);
                case "log":
                    Arity(name, a, 1, 2);
                    if (a.Count == 1) return Log(a[0]);
                    if (a[1] <= 0 || a[1] == 1) throw new MathException("log base must be positive and not 1");
                    return Log(a[0]) / Math.Log(a[1]);
                case "sin": Arity(name, a, 1); return Math.Sin(a[0]);
                case "cos": Arity(name, a, 1); return Math.Cos(a[0]);
                case "tan": Arity(name, a, 1); return Math.Tan(a[0]);
                case "round":
                    Arity(name, a, 1, 2);
                    if (a.Count == 1) return Math.Round(a[0], MidpointRounding.AwayFromZero);
                    var digits = (int)a[1];
                    if (digits < 0 || digits > 15) throw new MathException("round digits must be between 0 and 15");
                    return Math.Round(a[0], digits, MidpointRounding.AwayFromZero);
                case "clamp":
                    Arity(name, a, 3);
                    if (a[1] > a[2]) throw new MathException("clamp minimum is greater than maximum");
                    return Math.Clamp(a[0], a[1], a[2]);
                case "if": Arity(name, a, 3); return Truth(a[0]) ? a[1] : a[2];
                case "random":
                    Arity(name, a, 0, 2);
                    if (a.Count == 0) return random.NextDouble();
                    if (a.Count == 1) throw new MathException("random takes no arguments or two (min, max)");
                    var (lo, hi) = a[0] <= a[1] ? (a[0], a[1]) : (a[1], a[0]);
                    if (lo == Math.Truncate(lo) && hi == Math.Truncate(hi) && Math.Abs(lo) < 1e15 && Math.Abs(hi) < 1e15)
                    {
                        return random.NextInt64((long)lo, (long)hi + 1);
                    }
                    return lo + (random.NextDouble() * (hi - lo));
                default:
                    throw new MathException($"unknown function '{name}'");
            }
        }

        private static double Log(double x) =>
            x > 0 ? Math.Log(x) : throw new MathException("log of a number that is not positive");

        private static void Arity(string name, List<double> args, int min, int? max = null)
        {
            var upper = max ?? min;
            if (args.Count < min || args.Count > upper)
            {
                var expected = min == upper ? $"{min}" : upper == int.MaxValue ? $"at least {min}" : $"{min} to {upper}";
                throw new MathException($"{name} expects {expected} argument(s), got {args.Count}");
            }
        }

        private static bool Truth(double value) => value != 0 && !double.IsNaN(value);

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new MathException("expression is nested too deeply");
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Match(string token)
        {
            SkipSpaces();
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) != 0) return false;
            _pos += token.Length;
            return true;
        }

        /// <summary>Matches a one-character operator that is not the start of a two-character one.</summary>
        private bool MatchSingle(char c)
        {
            SkipSpaces();
            if (_pos >= text.Length || text[_pos] != c) return false;
            var next = _pos + 1 < text.Length ? text[_pos + 1] : '\0';
            var twoChar = c switch
            {
                '=' => next == '=',
                '<' => next is '=' or '>',
                '>' => next == '=',
                '!' => next == '=',
                '&' or '|' => true,
                _ => false,
            };
            if (twoChar) return false;
            _pos++;
            return true;
        }

        private void Expect(char c)
        {
            SkipSpaces();
            if (_pos >= text.Length) throw new MathException($"missing '{c}'");
            if (text[_pos] != c) throw new MathException($"expected '{c}' at position {_pos + 1} but found '{text[_pos]}'");
            _pos++;
        }
    }
}
