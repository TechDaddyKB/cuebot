using System.Globalization;

namespace Cuebot.Core.Variables;

/// <summary>A <c>$math()$</c> error; <see cref="IsSyntaxError"/> tells malformed text from a value that cannot be computed.</summary>
public sealed class MathException(string message, bool isSyntaxError = false) : Exception(message)
{
    public bool IsSyntaxError { get; } = isSyntaxError;
}

/// <summary>
/// A safe evaluator for <c>$math(...)$</c> (no code execution, no reflection) that accepts the mXparser 4.4 syntax
/// Streamer.bot documents for <c>$math()$</c>, plus Cuebot's own spellings.
/// <para>Precedence, lowest first: implication and equivalence <c>--&gt; &lt;-- -/&gt; &lt;/- &lt;-&gt;</c>;
/// or <c>| || \/</c>, nor <c>~| ~|| ~\/</c>, xor <c>(+)</c>; and <c>&amp; &amp;&amp; /\</c>, nand
/// <c>~&amp; ~&amp;&amp; ~/\</c>; equality <c>= == != &lt;&gt; ~=</c>; relations <c>&lt; &lt;= &gt; &gt;=</c>;
/// bitwise <c>@&amp; @^ @|</c>; shifts <c>@&lt;&lt; @&gt;&gt;</c>; <c>+ -</c>; <c>* / #</c> (and <c>%</c> between two
/// operands: modulo); unary <c>- + ! ~ @~</c>; <c>^</c> and tetration <c>^^</c> (right-associative, binding tighter
/// than unary minus, so <c>-2^2</c> is -4); postfix factorial <c>n!</c> and percentage <c>n%</c>. Boolean results are
/// 1 or 0; any non-zero value is true.</para>
/// <para>Numbers: <c>1.5</c>, <c>1.2e10</c>, <c>b.1010</c> / <c>b2.1010</c>, <c>o.17</c> / <c>b8.17</c>,
/// <c>h.1F</c> / <c>b16.1F</c>, any base <c>bN.digits</c> (1–36), fractions <c>1_2</c> and mixed <c>2_3_4</c>.
/// Constants: <c>pi e [gam] [phi] [true] [false] [NaN]</c>, <c>true false</c>. Random variables: <c>[Uni] [Int]
/// [Int1]…[Int9] [nat] [nat1]…[nat9] [Nat] [Nat1]…[Nat9] [Nor]</c>. mXparser's other bracketed constants, units,
/// metric prefixes and physical and astronomical constants (<c>[Kat] [km] [h] [%] [k] [c] [G.] [Earth-M]</c>…; see
/// <see cref="MathConstants"/>), in SI base units.</para>
/// <para>Functions (names are case-insensitive, except mXparser's <c>chi CHi Chi cHi</c>): trigonometric and
/// hyperbolic functions with their mXparser aliases (<c>sin cos tg/tan ctg/cot sec csc asin acos atan acot arcsec
/// sinh cosh tanh coth sech csch asinh acosh atanh acoth asech acsch</c>, …), <c>ln log2 log10 lg exp sqrt rad deg abs
/// sgn/sign floor ceil trunc not Gamma fact Fib Luc harm ispr isNaN ndig10</c>; <c>log(base, x)</c> (mXparser's
/// order; <c>log(x)</c> is ln), <c>mod root(n, x) C(n, k) KDelta pow round(x[, digits]) rUni(a, b) rUnid(a, b)
/// rNor(mean, sd) random() random(a, b)</c>; <c>if chi CHi Chi cHi clamp</c>; variadic <c>iff min max gcd lcm
/// add/sum multi mean var std med mode ndist argmin argmax rList coalesce and or xor base</c>. Arguments may be
/// separated by <c>,</c> or <c>;</c>. mXparser's special functions <c>erf erfc erfInv erfcInv Ei li Li Bell Pi(n)
/// LambW0 LambW1 logGamma sgnGamma diGamma GammaL GammaU GammaP GammaQ Beta logBeta BetaInc BetaI pNor cNor qNor pUni
/// cUni qUni Sa sinc Sinc nCk nPk Stirl2 Euler Harm(x, n) ndig dig dig10 nfact factval factexp ulp arccsc ConFrac
/// ConPol</c> (see <see cref="MathSpecialFunctions"/>).</para>
/// <para>Operators (see <c>MathEvaluator.Operators.cs</c>): <c>if</c> and <c>iff</c> evaluate only the branch they
/// return; iterated <c>sum prod avg vari stdi mini maxi (i, from, to, expression[, step])</c> (<c>sum</c> and
/// <c>avg</c> stay variadic unless the first argument is an index name); <c>der der- der+ (expression, x, point)</c>
/// (Ridders' extrapolation), <c>int(expression, x, a, b)</c> (adaptive Gauss–Kronrod), <c>solve(expression, x, a, b)</c>
/// (Brent), <c>diff difb (expression, x[, delta])</c> for a bound x. At most <see cref="MaxEvaluations"/>
/// sub-expression evaluations per expression.</para>
/// <para>Errors (syntax, unknown names, division or modulo by zero, results that are not finite numbers) throw
/// <see cref="MathException"/>; mXparser would return NaN instead.</para>
/// </summary>
public static partial class MathEvaluator
{
    public const int MaxLength = 4096;
    private const int MaxDepth = 64;

    /// <summary>Bound on the sub-expression evaluations of iterated and calculus operators in one expression.</summary>
    public const int MaxEvaluations = 100_000;

    public static double Evaluate(string expression, Random? random = null)
    {
        if (expression.Length > MaxLength) throw new MathException($"expression is longer than {MaxLength} characters");
        var parser = new Parser(expression, new Context(random ?? Random.Shared), null, 0, 0);
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

    /// <summary>State shared by a parser and the sub-parsers of its iterated and calculus operators.</summary>
    private sealed class Context(Random random)
    {
        public Random Random { get; } = random;

        /// <summary>Sub-expression evaluations so far (sum, prod, int, der, solve…), bounded by <see cref="MaxEvaluations"/>.</summary>
        public int Evaluations { get; set; }
    }

    /// <summary>A bound index or integration variable, innermost first.</summary>
    private sealed record Binding(string Name, double Value, Binding? Next)
    {
        public double? Find(string name)
        {
            for (var b = this; b is not null; b = b.Next)
                if (b.Name == name) return b.Value;
            return null;
        }
    }

    /// <param name="text">The expression, or one argument of an iterated or calculus operator.</param>
    /// <param name="offset">Where <paramref name="text"/> starts in the whole expression (for error positions).</param>
    private sealed partial class Parser(string text, Context context, Binding? variables, int depth, int offset)
    {
        private readonly Context _context = context;
        private readonly Binding? _variables = variables;
        private readonly Random random = context.Random;
        private readonly int _offset = offset;
        private int _pos;
        private int _depth = depth;

        public double ParseAll()
        {
            SkipSpaces();
            if (_pos >= text.Length) throw Syntax("expression is empty");
            var value = ParseImplication();
            SkipSpaces();
            if (_pos < text.Length) throw Syntax($"unexpected '{text[_pos]}' at position {_offset + _pos + 1}");
            return value;
        }

        private double ParseImplication()
        {
            var left = ParseOr();
            while (true)
            {
                if (Match("<->")) left = Truth(left) == Truth(ParseOr()) ? 1 : 0;
                else if (Match("-->")) left = !Truth(left) | Truth(ParseOr()) ? 1 : 0;
                else if (Match("<--")) left = Truth(left) | !Truth(ParseOr()) ? 1 : 0;
                else if (Match("-/>")) left = Truth(left) & !Truth(ParseOr()) ? 1 : 0;
                else if (Match("</-")) left = !Truth(left) & Truth(ParseOr()) ? 1 : 0;
                else return left;
            }
        }

        private double ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                if (Match("~||") || Match("~|") || Match("~\\/")) left = Truth(left) | Truth(ParseAnd()) ? 0 : 1;
                else if (Match("||") || MatchBare('|') || Match("\\/")) left = Truth(left) | Truth(ParseAnd()) ? 1 : 0;
                else if (Match("(+)")) left = Truth(left) ^ Truth(ParseAnd()) ? 1 : 0;
                else return left;
            }
        }

        private double ParseAnd()
        {
            var left = ParseEquality();
            while (true)
            {
                if (Match("~&&") || Match("~&") || Match("~/\\")) left = Truth(left) & Truth(ParseEquality()) ? 0 : 1;
                else if (Match("&&") || MatchBare('&') || Match("/\\")) left = Truth(left) & Truth(ParseEquality()) ? 1 : 0;
                else return left;
            }
        }

        private double ParseEquality()
        {
            var left = ParseComparison();
            while (true)
            {
                if (Match("==")) left = left == ParseComparison() ? 1 : 0;
                else if (Match("!=") || Match("<>") || Match("~=")) left = left != ParseComparison() ? 1 : 0;
                else if (MatchSingle('=')) left = left == ParseComparison() ? 1 : 0;
                else return left;
            }
        }

        private double ParseComparison()
        {
            var left = ParseBitwise();
            while (true)
            {
                if (Match("<=")) left = left <= ParseBitwise() ? 1 : 0;
                else if (Match(">=")) left = left >= ParseBitwise() ? 1 : 0;
                else if (MatchSingle('<')) left = left < ParseBitwise() ? 1 : 0;
                else if (MatchSingle('>')) left = left > ParseBitwise() ? 1 : 0;
                else return left;
            }
        }

        private double ParseBitwise()
        {
            var left = ParseShift();
            while (true)
            {
                if (Match("@&")) left = Integer(left, "@&") & Integer(ParseShift(), "@&");
                else if (Match("@^")) left = Integer(left, "@^") ^ Integer(ParseShift(), "@^");
                else if (Match("@|")) left = Integer(left, "@|") | Integer(ParseShift(), "@|");
                else return left;
            }
        }

        private double ParseShift()
        {
            var left = ParseAdditive();
            while (true)
            {
                if (Match("@<<")) left = Integer(left, "@<<") << (int)Math.Clamp(Integer(ParseAdditive(), "@<<"), 0, 63);
                else if (Match("@>>")) left = Integer(left, "@>>") >> (int)Math.Clamp(Integer(ParseAdditive(), "@>>"), 0, 63);
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
                else if (MatchSingle('#') || MatchSingle('%'))
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
                if (Match("@~")) return ~Integer(ParseUnary(), "@~");
                if (MatchSingle('-')) return -ParseUnary();
                if (MatchSingle('+')) return ParseUnary();
                if (MatchSingle('!') || MatchSingle('~')) return Truth(ParseUnary()) ? 0 : 1;
                return ParsePower();
            }
            finally
            {
                _depth--;
            }
        }

        private double ParsePower()
        {
            var value = ParsePostfix();
            if (Match("^^")) return Tetration(value, ParseUnary());
            if (MatchSingle('^')) value = Math.Pow(value, ParseUnary());
            return value;
        }

        /// <summary>A primary followed by any number of factorials (<c>n!</c>) and percentages (<c>n%</c>).</summary>
        private double ParsePostfix()
        {
            var value = ParsePrimary();
            while (true)
            {
                SkipSpaces();
                if (_pos >= text.Length) return value;
                var next = _pos + 1 < text.Length ? text[_pos + 1] : '\0';
                if (text[_pos] == '!' && next != '=')
                {
                    _pos++;
                    value = Factorial(value);
                }
                else if (text[_pos] == '%' && !OperandFollows(_pos + 1))
                {
                    _pos++;
                    value /= 100;
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>Whether an operand starts after <paramref name="at"/>: then <c>%</c> is modulo, not a percentage.</summary>
        private bool OperandFollows(int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at >= text.Length) return false;
            var c = text[at];
            return char.IsAsciiLetterOrDigit(c) || c is '.' or '(' or '[' or '_';
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= text.Length) throw Syntax("unexpected end of expression");
            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                Enter();
                var value = ParseImplication();
                _depth--;
                Expect(')');
                return value;
            }
            if (c == '[') return ParseBracketName();
            if (char.IsAsciiDigit(c) || c == '.') return ParseNumber();
            if (char.IsAsciiLetter(c) || c == '_') return ParseIdentifier();
            throw Syntax($"unexpected '{c}' at position {_offset + _pos + 1}");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.')) _pos++;

            // Fractions: 1_2 is 1/2, 2_3_4 is 2 + 3/4.
            if (_pos + 1 < text.Length && text[_pos] == '_' && char.IsAsciiDigit(text[_pos + 1]))
            {
                var parts = new List<double> { ParseDigits(start, _pos) };
                while (_pos + 1 < text.Length && text[_pos] == '_' && char.IsAsciiDigit(text[_pos + 1]))
                {
                    var from = ++_pos;
                    while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
                    parts.Add(ParseDigits(from, _pos));
                }
                if (parts.Count > 3) throw Syntax("a fraction has at most three parts (whole_numerator_denominator)");
                if (parts[^1] == 0) throw new MathException("division by zero");
                return parts.Count == 2 ? parts[0] / parts[1] : parts[0] + parts[1] / parts[2];
            }

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
            return ParseDigits(start, _pos);
        }

        private double ParseDigits(int start, int end)
        {
            var token = text.AsSpan(start, end - start);
            if (!double.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value))
            {
                throw Syntax($"'{token}' is not a number");
            }
            return value;
        }

        /// <summary><c>[name]</c>: mXparser's bracketed constants and random variables (case-sensitive, like mXparser).</summary>
        private double ParseBracketName()
        {
            var start = _pos;
            var end = text.IndexOf(']', _pos);
            if (end < 0) throw Syntax("missing ']'");
            _pos = end + 1;
            var name = text[(start + 1)..end];
            if (MathConstants.TryGet(name, out var constant)) return constant;
            switch (name)
            {
                case "Uni": return random.NextDouble();
                case "Nor": return Normal(0, 1);
                case "Int": return random.NextInt64(-1_000_000_000_000L, 1_000_000_000_001L);
                case "nat": return random.NextInt64(0, 1_000_000_000_001L);
                case "Nat": return random.NextInt64(1, 1_000_000_000_001L);
            }
            if (name.Length == 4 && name[3] is >= '1' and <= '9' && name[..3] is "Int" or "nat" or "Nat")
            {
                var bound = (long)Math.Pow(10, name[3] - '0');
                return name[..3] switch
                {
                    "Int" => random.NextInt64(-bound, bound + 1),
                    "nat" => random.NextInt64(0, bound + 1),
                    _ => random.NextInt64(1, bound + 1),
                };
            }
            throw Syntax($"unknown name '[{name}]'");
        }

        private double ParseIdentifier()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            var original = text.Substring(start, _pos - start);
            var name = original.ToLowerInvariant();

            // Number literals in other bases: b.1010, o.17, h.1F, b16.1F, b1.111 (unary).
            if (_pos < text.Length && text[_pos] == '.' && BaseOf(name) is { } numberBase) return ParseBaseNumber(numberBase);

            // der- and der+ (one-sided derivatives)
            if (name == "der" && _pos + 1 < text.Length && text[_pos] is '-' or '+' && text[_pos + 1] == '(') name += text[_pos++];

            SkipSpaces();
            if (_pos < text.Length && text[_pos] == '(' && IsOperator(name))
            {
                _pos++;
                Enter();
                var result = Operator(name, SplitArguments());
                _depth--;
                return result;
            }
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
                    do args.Add(ParseImplication());
                    while (MatchSingle(',') || MatchSingle(';'));
                    Expect(')');
                }
                _depth--;
                return name switch
                {
                    "chi" => Characteristic(original, args),
                    "sinc" or "li" => CaseSensitive(original, args),
                    _ => Call(name, args),
                };
            }
            if (_variables?.Find(original) is { } bound) return bound;
            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                "true" => 1,
                "false" => 0,
                _ => throw Syntax($"unknown name '{original}'"),
            };
        }

        private static int? BaseOf(string name)
        {
            if (name == "b") return 2;
            if (name == "o") return 8;
            if (name == "h") return 16;
            return name.Length is 2 or 3 && name[0] == 'b'
                   && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 36
                ? n
                : null;
        }

        private double ParseBaseNumber(int numberBase)
        {
            _pos++; // the '.'
            var start = _pos;
            while (_pos < text.Length && char.IsAsciiLetterOrDigit(text[_pos])) _pos++;
            if (_pos == start) throw Syntax("missing digits after the number base");
            var digits = text[start.._pos];
            if (numberBase == 1)
            {
                if (digits.Any(d => d != '1')) throw Syntax($"'{digits}' is not a unary number");
                return digits.Length;
            }
            double value = 0;
            foreach (var d in digits)
            {
                var digit = char.IsAsciiDigit(d) ? d - '0' : char.ToLowerInvariant(d) - 'a' + 10;
                if (digit >= numberBase) throw Syntax($"'{digits}' is not a base-{numberBase} number");
                value = value * numberBase + digit;
            }
            return value;
        }

        /// <summary>mXparser's characteristic functions; the capital letters say which bounds are included.</summary>
        private static double Characteristic(string name, List<double> a)
        {
            Arity(name, a, 3);
            var (x, lo, hi) = (a[0], a[1], a[2]);
            var inside = name switch
            {
                "chi" => x > lo && x < hi,     // (a, b)
                "CHi" => x >= lo && x <= hi,   // [a, b]
                "Chi" => x >= lo && x < hi,    // [a, b)
                "cHi" => x > lo && x <= hi,    // (a, b]
                _ => throw Syntax($"unknown function '{name}' (use chi, CHi, Chi or cHi)"),
            };
            return inside ? 1 : 0;
        }

        private double Call(string name, List<double> a)
        {
            switch (name)
            {
                // ---- Cuebot's own and shared names
                case "min": Arity(name, a, 1, int.MaxValue); return a.Min();
                case "max": Arity(name, a, 1, int.MaxValue); return a.Max();
                case "abs": Arity(name, a, 1); return Math.Abs(a[0]);
                case "floor": Arity(name, a, 1); return Math.Floor(a[0]);
                case "ceil": Arity(name, a, 1); return Math.Ceiling(a[0]);
                case "trunc": Arity(name, a, 1); return Math.Truncate(a[0]);
                case "sign" or "sgn": Arity(name, a, 1); return Math.Sign(a[0]);
                case "sqrt":
                    Arity(name, a, 1);
                    if (a[0] < 0) throw new MathException("sqrt of a negative number");
                    return Math.Sqrt(a[0]);
                case "pow": Arity(name, a, 2); return Math.Pow(a[0], a[1]);
                case "exp": Arity(name, a, 1); return Math.Exp(a[0]);
                case "ln": Arity(name, a, 1); return Log(a[0]);
                case "log10" or "lg": Arity(name, a, 1); return Log(a[0]) / Math.Log(10);
                case "log2": Arity(name, a, 1); return Log(a[0]) / Math.Log(2);
                case "log":
                    // mXparser's order: log(a, b) is the logarithm of b to base a. One argument: natural logarithm.
                    Arity(name, a, 1, 2);
                    if (a.Count == 1) return Log(a[0]);
                    if (a[0] <= 0 || a[0] == 1) throw new MathException("log base must be positive and not 1");
                    return Log(a[1]) / Math.Log(a[0]);
                case "round":
                    Arity(name, a, 1, 2);
                    return Round(a[0], a.Count == 2 ? a[1] : 0);
                case "clamp":
                    Arity(name, a, 3);
                    if (a[1] > a[2]) throw new MathException("clamp minimum is greater than maximum");
                    return Math.Clamp(a[0], a[1], a[2]);
                case "random":
                    Arity(name, a, 0, 2);
                    if (a.Count == 0) return random.NextDouble();
                    if (a.Count == 1) throw new MathException("random takes no arguments or two (min, max)");
                    var (lo, hi) = a[0] <= a[1] ? (a[0], a[1]) : (a[1], a[0]);
                    if (IsInteger(lo) && IsInteger(hi)) return random.NextInt64((long)lo, (long)hi + 1);
                    return lo + (random.NextDouble() * (hi - lo));

                // ---- trigonometry (radians) and hyperbolic functions
                case "sin": Arity(name, a, 1); return Math.Sin(a[0]);
                case "cos": Arity(name, a, 1); return Math.Cos(a[0]);
                case "tan" or "tg": Arity(name, a, 1); return Math.Tan(a[0]);
                case "ctg" or "cot" or "ctan": Arity(name, a, 1); return 1 / Math.Tan(a[0]);
                case "sec": Arity(name, a, 1); return 1 / Math.Cos(a[0]);
                case "csc" or "cosec": Arity(name, a, 1); return 1 / Math.Sin(a[0]);
                case "asin" or "arsin" or "arcsin": Arity(name, a, 1); return Math.Asin(a[0]);
                case "acos" or "arcos" or "arccos": Arity(name, a, 1); return Math.Acos(a[0]);
                case "atan" or "atg" or "arctg" or "arctan": Arity(name, a, 1); return Math.Atan(a[0]);
                case "acot" or "actg" or "actan" or "arcctg" or "arccot" or "arcctan":
                    Arity(name, a, 1); return Math.PI / 2 - Math.Atan(a[0]);
                case "arcsec": Arity(name, a, 1); return Math.Acos(1 / a[0]);
                case "sinh": Arity(name, a, 1); return Math.Sinh(a[0]);
                case "cosh": Arity(name, a, 1); return Math.Cosh(a[0]);
                case "tanh" or "tgh": Arity(name, a, 1); return Math.Tanh(a[0]);
                case "coth" or "ctgh" or "ctanh": Arity(name, a, 1); return 1 / Math.Tanh(a[0]);
                case "sech": Arity(name, a, 1); return 1 / Math.Cosh(a[0]);
                case "csch" or "cosech": Arity(name, a, 1); return 1 / Math.Sinh(a[0]);
                case "asinh" or "arsinh" or "arcsinh": Arity(name, a, 1); return Math.Asinh(a[0]);
                case "acosh" or "arcosh" or "arccosh": Arity(name, a, 1); return Math.Acosh(a[0]);
                case "atanh" or "atgh" or "arctgh" or "arctanh": Arity(name, a, 1); return Math.Atanh(a[0]);
                case "acoth" or "actgh" or "actanh" or "arcoth" or "arccoth" or "arcctgh" or "arcctanh":
                    Arity(name, a, 1); return 0.5 * Math.Log((a[0] + 1) / (a[0] - 1));
                case "asech" or "arsech" or "arcsech": Arity(name, a, 1); return Math.Acosh(1 / a[0]);
                case "acsch" or "arcsch" or "arccsch" or "acosech" or "arcosech": Arity(name, a, 1); return Math.Asinh(1 / a[0]);
                case "rad": Arity(name, a, 1); return a[0] * Math.PI / 180;
                case "deg": Arity(name, a, 1); return a[0] * 180 / Math.PI;

                // ---- number functions
                case "not": Arity(name, a, 1); return Truth(a[0]) ? 0 : 1;
                case "isnan": Arity(name, a, 1); return double.IsNaN(a[0]) ? 1 : 0;
                case "fact": Arity(name, a, 1); return Factorial(a[0]);
                case "gamma": Arity(name, a, 1); return Gamma(a[0]);
                case "fib": Arity(name, a, 1); return Fibonacci(NonNegative(name, a[0]));
                case "luc": Arity(name, a, 1); return Lucas(NonNegative(name, a[0]));
                case "harm" when a.Count == 2: return Special(name, a, "Harm");
                case "harm":
                    Arity(name, a, 1);
                    double h = 0;
                    for (var k = NonNegative(name, a[0]); k >= 1; k--) h += 1.0 / k;
                    return h;
                case "ispr": Arity(name, a, 1); return IsPrime(a[0]) ? 1 : 0;
                case "ndig10":
                    Arity(name, a, 1);
                    var whole = Math.Floor(Math.Abs(a[0]));
                    return whole < 1 ? 1 : Math.Floor(Math.Log10(whole)) + 1;
                case "mod":
                    Arity(name, a, 2);
                    if (a[1] == 0) throw new MathException("modulo by zero");
                    return a[0] % a[1];
                case "root":
                    Arity(name, a, 2);
                    if (a[0] == 0) throw new MathException("root order must not be 0");
                    if (a[1] < 0)
                    {
                        // Odd roots of negative numbers are real: root(3, -8) = -2.
                        if (IsInteger(a[0]) && Math.Abs(a[0] % 2) == 1) return -Math.Pow(-a[1], 1 / a[0]);
                        throw new MathException("even root of a negative number");
                    }
                    return Math.Pow(a[1], 1 / a[0]);
                case "c": Arity(name, a, 2); return Binomial(a[0], a[1]);
                case "kdelta": Arity(name, a, 2); return a[0] == a[1] ? 1 : 0;
                case "runi": Arity(name, a, 2); return a[0] + random.NextDouble() * (a[1] - a[0]);
                case "runid":
                    Arity(name, a, 2);
                    var from = Math.Ceiling(Math.Min(a[0], a[1]));
                    var to = Math.Floor(Math.Max(a[0], a[1]));
                    if (from > to) throw new MathException("rUnid needs a whole number between its bounds");
                    return random.NextInt64((long)from, (long)to + 1);
                case "rnor": Arity(name, a, 2); return Normal(a[0], a[1]);

                // ---- variadic
                case "gcd": Arity(name, a, 1, int.MaxValue); return a.Select(x => Integer(x, name)).Aggregate(Gcd);
                case "lcm":
                    Arity(name, a, 1, int.MaxValue);
                    return a.Select(x => Integer(x, name)).Aggregate((x, y) => x == 0 || y == 0 ? 0 : Math.Abs(x / Gcd(x, y) * y));
                case "add" or "sum": Arity(name, a, 1, int.MaxValue); return a.Sum();
                case "multi": Arity(name, a, 1, int.MaxValue); return a.Aggregate(1.0, (x, y) => x * y);
                case "mean" or "avg": Arity(name, a, 1, int.MaxValue); return a.Average();
                case "var": Arity(name, a, 1, int.MaxValue); return Variance(a);
                case "std": Arity(name, a, 1, int.MaxValue); return Math.Sqrt(Variance(a));
                case "med":
                    Arity(name, a, 1, int.MaxValue);
                    var sorted = a.Order().ToArray();
                    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
                case "mode":
                    Arity(name, a, 1, int.MaxValue);
                    return a.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => a.IndexOf(g.Key)).First().Key;
                case "ndist": Arity(name, a, 1, int.MaxValue); return a.Distinct().Count();
                case "argmin": Arity(name, a, 1, int.MaxValue); return a.IndexOf(a.Min()) + 1;
                case "argmax": Arity(name, a, 1, int.MaxValue); return a.IndexOf(a.Max()) + 1;
                case "rlist": Arity(name, a, 1, int.MaxValue); return a[random.Next(a.Count)];
                case "coalesce": Arity(name, a, 1, int.MaxValue); return a.FirstOrDefault(x => !double.IsNaN(x), double.NaN);
                case "and": Arity(name, a, 1, int.MaxValue); return a.All(Truth) ? 1 : 0;
                case "or": Arity(name, a, 1, int.MaxValue); return a.Any(Truth) ? 1 : 0;
                case "xor": Arity(name, a, 1, int.MaxValue); return a.Count(Truth) % 2 == 1 ? 1 : 0;
                case "base":
                    Arity(name, a, 2, int.MaxValue);
                    var b = Integer(a[0], name);
                    if (b is < 1 or > 36) throw new MathException("base must be between 1 and 36");
                    return a.Skip(1).Aggregate(0.0, (acc, d) => acc * b + d);
                default:
                    return Special(name, a);
            }
        }

        private static double Round(double value, double places)
        {
            var digits = (int)places;
            if (digits is < -15 or > 15) throw new MathException("round digits must be between -15 and 15");
            if (digits >= 0) return Math.Round(value, digits, MidpointRounding.AwayFromZero);
            var scale = Math.Pow(10, -digits);
            return Math.Round(value / scale, MidpointRounding.AwayFromZero) * scale;
        }

        /// <summary>n! for whole numbers; Γ(n+1) otherwise, as mXparser does.</summary>
        private static double Factorial(double n)
        {
            if (n < 0) throw new MathException("factorial of a negative number");
            if (!IsInteger(n)) return Gamma(n + 1);
            if (n > 170) throw new MathException("factorial is too large");
            double result = 1;
            for (var i = 2; i <= (int)n; i++) result *= i;
            return result;
        }

        /// <summary>
        /// Γ(x): exact for whole numbers, the reflection formula below 0.5, otherwise Stirling's series after shifting
        /// x to at least 10 (error near 1e-15).
        /// </summary>
        private static double Gamma(double x)
        {
            if (IsInteger(x) && x <= 0) throw new MathException("Gamma is undefined at 0 and negative whole numbers");
            if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
            if (IsInteger(x) && x <= 171) return Factorial(x - 1);
            var shift = 1.0;
            while (x < 10) shift *= x++;
            var z2 = 1 / (x * x);
            var series = (1.0 / 12 - z2 * (1.0 / 360 - z2 * (1.0 / 1260 - z2 * (1.0 / 1680 - z2 * (1.0 / 1188 - z2 * (691.0 / 360360 - z2 / 156)))))) / x;
            return Math.Exp((x - 0.5) * Math.Log(x) - x + 0.5 * Math.Log(2 * Math.PI) + series) / shift;
        }

        private static double Binomial(double n, double k)
        {
            if (!IsInteger(k) || k < 0) throw new MathException("C(n, k) needs a whole k of at least 0");
            if (IsInteger(n) && n >= 0 && k > n) return 0;
            double result = 1;
            for (var i = 1; i <= (int)k; i++) result = result * (n - k + i) / i;
            return IsInteger(n) ? Math.Round(result) : result;
        }

        private static double Fibonacci(int n)
        {
            double a = 0, b = 1;
            for (var i = 0; i < n; i++) (a, b) = (b, a + b);
            return a;
        }

        private static double Lucas(int n)
        {
            double a = 2, b = 1;
            for (var i = 0; i < n; i++) (a, b) = (b, a + b);
            return a;
        }

        private static bool IsPrime(double value)
        {
            if (!IsInteger(value) || value < 2) return false;
            var n = (long)value;
            if (n % 2 == 0) return n == 2;
            for (long d = 3; d * d <= n; d += 2)
                if (n % d == 0) return false;
            return true;
        }

        private static double Variance(List<double> a)
        {
            if (a.Count < 2) return double.NaN;
            var mean = a.Average();
            return a.Sum(x => (x - mean) * (x - mean)) / (a.Count - 1);
        }

        /// <summary>A normally distributed random number (Box–Muller).</summary>
        private double Normal(double mean, double sd)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        private static double Tetration(double a, double n)
        {
            if (!IsInteger(n) || n < 0) throw new MathException("tetration needs a whole height of at least 0");
            double result = 1;
            for (var i = 0; i < (int)n && double.IsFinite(result); i++) result = Math.Pow(a, result);
            return result;
        }

        private static long Gcd(long x, long y)
        {
            x = Math.Abs(x);
            y = Math.Abs(y);
            while (y != 0) (x, y) = (y, x % y);
            return x;
        }

        private static bool IsInteger(double x) => x == Math.Truncate(x) && Math.Abs(x) < 1e15;

        private static long Integer(double x, string op) =>
            IsInteger(x) ? (long)x : throw new MathException($"{op} needs whole numbers");

        private static int NonNegative(string name, double x) =>
            IsInteger(x) && x is >= 0 and <= 1000 ? (int)x : throw new MathException($"{name} needs a whole number from 0 to 1000");

        private static double Log(double x) =>
            x > 0 ? Math.Log(x) : throw new MathException("log of a number that is not positive");

        private static void Arity(string name, List<double> args, int min, int? max = null) => Arity(name, args.Count, min, max);

        private static void Arity(string name, int count, int min, int? max = null)
        {
            var upper = max ?? min;
            if (count < min || count > upper)
            {
                var expected = min == upper ? $"{min}" : upper == int.MaxValue ? $"at least {min}" : $"{min} to {upper}";
                throw Syntax($"{name} expects {expected} argument(s), got {count}");
            }
        }

        private static MathException Syntax(string message) => new(message, isSyntaxError: true);

        private static bool Truth(double value) => value != 0 && !double.IsNaN(value);

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Syntax("expression is nested too deeply");
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

        /// <summary>A single <c>&amp;</c> or <c>|</c> (not the first half of <c>&amp;&amp;</c> / <c>||</c>).</summary>
        private bool MatchBare(char c)
        {
            SkipSpaces();
            if (_pos >= text.Length || text[_pos] != c) return false;
            if (_pos + 1 < text.Length && text[_pos + 1] == c) return false;
            _pos++;
            return true;
        }

        /// <summary>Matches a one-character operator that is not the start of a longer one.</summary>
        private bool MatchSingle(char c)
        {
            SkipSpaces();
            if (_pos >= text.Length || text[_pos] != c) return false;
            var rest = text.AsSpan(_pos);
            var longer = c switch
            {
                '=' => rest.StartsWith("=="),
                '<' => rest.StartsWith("<=") || rest.StartsWith("<>") || rest.StartsWith("<--") || rest.StartsWith("<->") || rest.StartsWith("</-"),
                '>' => rest.StartsWith(">="),
                '!' => rest.StartsWith("!="),
                '-' => rest.StartsWith("-->") || rest.StartsWith("-/>"),
                '/' => rest.StartsWith("/\\"),
                '~' => rest.StartsWith("~=") || rest.StartsWith("~&") || rest.StartsWith("~|") || rest.StartsWith("~/\\") || rest.StartsWith("~\\/"),
                '^' => rest.StartsWith("^^"),
                _ => false,
            };
            if (longer) return false;
            _pos++;
            return true;
        }

        private void Expect(char c)
        {
            SkipSpaces();
            if (_pos >= text.Length) throw Syntax($"missing '{c}'");
            if (text[_pos] != c) throw Syntax($"expected '{c}' at position {_offset + _pos + 1} but found '{text[_pos]}'");
            _pos++;
        }
    }
}
