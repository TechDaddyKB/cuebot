using System.Text.RegularExpressions;
using S = Cuebot.Core.Variables.MathSpecialFunctions;

namespace Cuebot.Core.Variables;

public static partial class MathEvaluator
{
    private sealed partial class Parser
    {
        /// <summary>A range of <c>text</c>: one argument of an operator whose arguments are evaluated lazily or repeatedly.</summary>
        private readonly record struct Segment(int Start, int End);

        /// <summary>
        /// Functions whose arguments are not simply evaluated once, left to right: <c>if</c> and <c>iff</c> evaluate only the
        /// branch they return; iterated operators evaluate their expression for every index; calculus operators evaluate
        /// theirs at the points their numeric method needs.
        /// </summary>
        private static bool IsOperator(string name) => name is "if" or "iff" or "sum" or "prod" or "avg" or "vari" or "stdi" or "mini"
            or "maxi" or "int" or "der" or "der-" or "der+" or "solve" or "diff" or "difb";

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
        private static partial Regex VariableNameRegex();

        /// <summary>Splits the arguments after <c>name(</c> at top-level <c>,</c> and <c>;</c>, and moves past the closing <c>)</c>.</summary>
        private List<Segment> SplitArguments()
        {
            var segments = new List<Segment>();
            var start = _pos;
            var nesting = 0;
            for (var i = _pos; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '(':
                        nesting++;
                        break;
                    case '[':
                        var close = text.IndexOf(']', i);
                        if (close < 0) throw Syntax("missing ']'");
                        i = close;
                        break;
                    case ')' when nesting > 0:
                        nesting--;
                        break;
                    case ')':
                        if (segments.Count > 0 || !string.IsNullOrWhiteSpace(text[start..i])) segments.Add(new Segment(start, i));
                        _pos = i + 1;
                        return segments;
                    case ',' or ';' when nesting == 0:
                        segments.Add(new Segment(start, i));
                        start = i + 1;
                        break;
                }
            }
            throw Syntax("missing ')'");
        }

        private double Value(Segment s, Binding? scope)
        {
            if (++_context.Evaluations > MaxEvaluations)
                throw new MathException($"the expression needs more than {MaxEvaluations} evaluations (sum, prod, int, der, solve…)");
            return new Parser(text[s.Start..s.End], _context, scope, _depth, _offset + s.Start).ParseAll();
        }

        private double Value(Segment s) => Value(s, _variables);

        /// <summary>Checks an argument whose value is not used (the other branch of <c>if</c>): only syntax errors count.</summary>
        private void Check(Segment s)
        {
            try
            {
                Value(s);
            }
            catch (MathException ex) when (!ex.IsSyntaxError)
            {
            }
        }

        private string? VariableName(Segment s)
        {
            var name = text[s.Start..s.End].Trim();
            return VariableNameRegex().IsMatch(name) ? name : null;
        }

        /// <summary>The expression in <paramref name="body"/> as a function of <paramref name="variable"/>.</summary>
        private Func<double, double> Function(Segment body, string variable) => x => Value(body, new Binding(variable, x, _variables));

        /// <summary><c>sum(i, 1, 10, i)</c> rather than Cuebot's variadic <c>sum(1, 2, 3, 4)</c>.</summary>
        private bool IsIteratedForm(List<Segment> s) =>
            s.Count is 4 or 5 && VariableName(s[0]) is { } v && v.ToLowerInvariant() is not ("pi" or "e" or "true" or "false");

        private double Operator(string name, List<Segment> s)
        {
            switch (name)
            {
                case "if":
                {
                    Arity(name, s.Count, 3);
                    var condition = Truth(Value(s[0]));
                    var result = Value(s[condition ? 1 : 2]);
                    Check(s[condition ? 2 : 1]);
                    return result;
                }
                case "iff":
                {
                    Arity(name, s.Count, 2, int.MaxValue);
                    if (s.Count % 2 != 0) throw Syntax("iff expects condition, value pairs");
                    double? result = null;
                    for (var i = 0; i < s.Count; i += 2)
                    {
                        if (result is null && Truth(Value(s[i])))
                        {
                            result = Value(s[i + 1]);
                            continue;
                        }
                        if (result is not null) Check(s[i]);
                        Check(s[i + 1]);
                    }
                    return result ?? double.NaN;
                }
                case "sum" or "avg" when !IsIteratedForm(s):
                    return Call(name, s.Select(Value).ToList());
                case "sum" or "prod" or "avg" or "vari" or "stdi" or "mini" or "maxi":
                    return Iterated(name, s);
                case "der" or "der-" or "der+":
                {
                    Arity(name, s.Count, 2, 3);
                    var variable = VariableName(s[1]) ?? throw Syntax($"{name}'s second argument is the variable, as in {name}(x^2, x, 3)");
                    var at = s.Count == 3 ? Value(s[2])
                        : _variables?.Find(variable) ?? throw Syntax($"{name} needs the point: {name}(expression, {variable}, point)");
                    return Derivative(Function(s[0], variable), at, name == "der" ? 0 : name == "der+" ? 1 : -1);
                }
                case "diff" or "difb":
                {
                    Arity(name, s.Count, 2, 3);
                    var variable = VariableName(s[1]) ?? throw Syntax($"{name}'s second argument is the variable");
                    var x = _variables?.Find(variable)
                        ?? throw Syntax($"{name} needs {variable} to have a value (use it inside sum, prod, int…)");
                    var delta = s.Count == 3 ? Value(s[2]) : 1;
                    var f = Function(s[0], variable);
                    return name == "diff" ? f(x + delta) - f(x) : f(x) - f(x - delta);
                }
                case "int" or "solve":
                {
                    Arity(name, s.Count, 4);
                    var variable = VariableName(s[1]) ?? throw Syntax($"{name}'s second argument is the variable, as in {name}(x^2, x, 0, 1)");
                    double from = Value(s[2]), to = Value(s[3]);
                    if (!double.IsFinite(from) || !double.IsFinite(to)) throw new MathException($"{name} needs finite bounds");
                    var f = Function(s[0], variable);
                    return name == "int" ? Integrate(f, from, to) : Solve(f, from, to);
                }
                default:
                    throw Syntax($"unknown function '{name}'");
            }
        }

        /// <summary>
        /// <c>sum prod avg vari stdi mini maxi (i, from, to, expression[, step])</c>: the index runs from <c>from</c> to
        /// <c>to</c> (either direction) in steps of <c>step</c> (default 1).
        /// </summary>
        private double Iterated(string name, List<Segment> s)
        {
            Arity(name, s.Count, 4, 5);
            var variable = VariableName(s[0]) ?? throw Syntax($"{name}'s first argument is the index, as in {name}(i, 1, 10, i^2)");
            double from = Value(s[1]), to = Value(s[2]);
            var step = s.Count == 5 ? Math.Abs(Value(s[4])) : 1;
            if (!double.IsFinite(from) || !double.IsFinite(to)) throw new MathException($"{name} needs finite bounds");
            if (!(step > 0) || !double.IsFinite(step)) throw new MathException($"{name} needs a step other than 0");
            var count = Math.Floor(Math.Abs(to - from) / step + 1e-9) + 1;
            if (count > MaxEvaluations) throw new MathException($"{name} would take more than {MaxEvaluations} steps");
            var direction = to >= from ? 1 : -1;
            var f = Function(s[3], variable);
            var values = new List<double>((int)count);
            for (var k = 0; k < (int)count; k++) values.Add(f(from + direction * k * step));
            return name switch
            {
                "sum" => values.Sum(),
                "prod" => values.Aggregate(1.0, (x, y) => x * y),
                "avg" => values.Average(),
                "vari" => Variance(values),
                "stdi" => Math.Sqrt(Variance(values)),
                "mini" => values.Min(),
                _ => values.Max(),
            };
        }

        /// <summary>
        /// Ridders' method: finite differences with shrinking steps, extrapolated to step 0 (Richardson). Central for
        /// <c>der</c> (<paramref name="side"/> 0), one-sided for <c>der+</c> (1) and <c>der-</c> (-1).
        /// </summary>
        private static double Derivative(Func<double, double> f, double x, int side)
        {
            const int n = 10;
            const double shrink = 1.4;
            var order = side == 0 ? shrink * shrink : shrink;   // error terms in h² (central) or h (one-sided)
            var fx = side == 0 ? 0 : f(x);
            double Difference(double h) => side switch
            {
                0 => (f(x + h) - f(x - h)) / (2 * h),
                > 0 => (f(x + h) - fx) / h,
                _ => (fx - f(x - h)) / h,
            };

            var table = new double[n, n];
            var step = 0.1 * Math.Max(Math.Abs(x), 1e-3);
            table[0, 0] = Difference(step);
            var best = table[0, 0];
            var error = double.MaxValue;
            for (var i = 1; i < n; i++)
            {
                step /= shrink;
                table[0, i] = Difference(step);
                var factor = order;
                for (var j = 1; j <= i; j++)
                {
                    table[j, i] = (table[j - 1, i] * factor - table[j - 1, i - 1]) / (factor - 1);
                    factor *= order;
                    var estimate = Math.Max(Math.Abs(table[j, i] - table[j - 1, i]), Math.Abs(table[j, i] - table[j - 1, i - 1]));
                    if (estimate <= error)
                    {
                        error = estimate;
                        best = table[j, i];
                    }
                }
                if (Math.Abs(table[i, i] - table[i - 1, i - 1]) >= 2 * error) break;
            }
            return best;
        }

        private static readonly double[] KronrodNodes =
        [
            0.991455371120812639206854697526329, 0.949107912342758524526189684047851, 0.864864423359769072789712788640926,
            0.741531185599394439863864773280788, 0.586087235467691130294144845693013, 0.405845151377397166906606412076961,
            0.207784955007898467600689403773245, 0,
        ];

        private static readonly double[] KronrodWeights =
        [
            0.022935322010529224963732008058970, 0.063092092629978553290700663189204, 0.104790010322250183839876322541518,
            0.140653259715525918745189590510238, 0.169004726639267902826583426598550, 0.190350578064785409913256402421014,
            0.204432940075298892414161999234649, 0.209482141084727828012999174891714,
        ];

        /// <summary>7-point Gauss weights, for the Kronrod nodes 1, 3, 5 and 7 (the centre).</summary>
        private static readonly double[] GaussWeights =
        [
            0.129484966168869693270611432679082, 0.279705391489276667901467771423780, 0.381830050505118944950369775488975,
            0.417959183673469387755102040816327,
        ];

        /// <summary>Gauss–Kronrod 7/15 on one interval: the estimate and the difference from the Gauss rule.</summary>
        private static (double Value, double Error) Kronrod(Func<double, double> f, double a, double b)
        {
            var centre = (a + b) / 2;
            var half = (b - a) / 2;
            var fc = f(centre);
            var kronrod = fc * KronrodWeights[7];
            var gauss = fc * GaussWeights[3];
            for (var i = 0; i < 7; i++)
            {
                var dx = half * KronrodNodes[i];
                var sum = f(centre - dx) + f(centre + dx);
                kronrod += KronrodWeights[i] * sum;
                if (i % 2 == 1) gauss += GaussWeights[i / 2] * sum;
            }
            return (kronrod * half, Math.Abs((kronrod - gauss) * half));
        }

        /// <summary>Adaptive Gauss–Kronrod: keeps splitting the interval with the largest error (at most 200 intervals).</summary>
        private static double Integrate(Func<double, double> f, double a, double b)
        {
            if (a == b) return 0;
            if (a > b) return -Integrate(f, b, a);
            var parts = new List<(double A, double B, double Value, double Error)>();
            var (value, error) = Kronrod(f, a, b);
            parts.Add((a, b, value, error));
            while (parts.Count < 200)
            {
                var total = parts.Sum(p => p.Value);
                var totalError = parts.Sum(p => p.Error);
                if (!double.IsFinite(total) || totalError <= Math.Max(1e-14, 1e-12 * Math.Abs(total))) break;
                var worst = 0;
                for (var i = 1; i < parts.Count; i++)
                    if (parts[i].Error > parts[worst].Error) worst = i;
                var (from, to, _, _) = parts[worst];
                var middle = (from + to) / 2;
                if (middle <= from || middle >= to) break;
                var left = Kronrod(f, from, middle);
                var right = Kronrod(f, middle, to);
                parts[worst] = (from, middle, left.Value, left.Error);
                parts.Add((middle, to, right.Value, right.Error));
            }
            return parts.Sum(p => p.Value);
        }

        /// <summary>A root of f between a and b: Brent's method, after looking for a sign change in 100 sub-intervals if needed.</summary>
        private static double Solve(Func<double, double> f, double a, double b)
        {
            if (a > b) (a, b) = (b, a);
            double F(double x)
            {
                var y = f(x);
                return double.IsNaN(y) ? throw new MathException($"solve: the expression is not a number at {FormatNumber(x)}") : y;
            }

            double fa = F(a), fb = F(b);
            if (fa == 0) return a;
            if (fb == 0) return b;
            if (Math.Sign(fa) == Math.Sign(fb))
            {
                var found = false;
                double x0 = a, f0 = fa;
                for (var i = 1; i <= 100 && !found; i++)
                {
                    var x1 = i == 100 ? b : a + (b - a) * i / 100;
                    var f1 = F(x1);
                    if (f1 == 0) return x1;
                    if (Math.Sign(f1) != Math.Sign(f0))
                    {
                        (a, fa, b, fb) = (x0, f0, x1, f1);
                        found = true;
                    }
                    (x0, f0) = (x1, f1);
                }
                if (!found) throw new MathException("solve found no root: the expression does not change sign between the bounds");
            }

            double c = b, fc = fb, d = b - a, e = d;
            for (var iteration = 0; iteration < 200; iteration++)
            {
                if ((fb > 0 && fc > 0) || (fb < 0 && fc < 0))
                {
                    (c, fc) = (a, fa);
                    e = d = b - a;
                }
                if (Math.Abs(fc) < Math.Abs(fb))
                {
                    (a, b, c) = (b, c, b);
                    (fa, fb, fc) = (fb, fc, fb);
                }
                var tolerance = 2 * 1e-16 * Math.Abs(b) + 0.5e-15;
                var middle = 0.5 * (c - b);
                if (Math.Abs(middle) <= tolerance || fb == 0) return b;
                if (Math.Abs(e) >= tolerance && Math.Abs(fa) > Math.Abs(fb))
                {
                    double p, q;
                    var s = fb / fa;
                    if (a == c)
                    {
                        p = 2 * middle * s;
                        q = 1 - s;
                    }
                    else
                    {
                        var qa = fa / fc;
                        var r = fb / fc;
                        p = s * (2 * middle * qa * (qa - r) - (b - a) * (r - 1));
                        q = (qa - 1) * (r - 1) * (s - 1);
                    }
                    if (p > 0) q = -q;
                    p = Math.Abs(p);
                    if (2 * p < Math.Min(3 * middle * q - Math.Abs(tolerance * q), Math.Abs(e * q)))
                    {
                        e = d;
                        d = p / q;
                    }
                    else
                    {
                        d = middle;
                        e = d;
                    }
                }
                else
                {
                    d = middle;
                    e = d;
                }
                (a, fa) = (b, fb);
                b += Math.Abs(d) > tolerance ? d : middle >= 0 ? tolerance : -tolerance;
                fb = F(b);
            }
            return b;
        }

        /// <summary>mXparser names that differ only in case: <c>sinc</c> / <c>Sinc</c>, <c>li</c> / <c>Li</c>.</summary>
        private static double CaseSensitive(string original, List<double> a) => original switch
        {
            "Sinc" => Special("sinc-unnormalized", a, original),
            "Li" => Special("li-offset", a, original),
            _ => Special(original.ToLowerInvariant(), a, original),
        };

        private static readonly Dictionary<string, (int Min, int Max)> SpecialArity = new(StringComparer.Ordinal)
        {
            ["sa"] = (1, 1), ["sinc"] = (1, 1), ["sinc-unnormalized"] = (1, 1), ["bell"] = (1, 1), ["pi"] = (1, 1), ["ei"] = (1, 1),
            ["li"] = (1, 1), ["li-offset"] = (1, 1), ["erf"] = (1, 1), ["erfc"] = (1, 1), ["erfinv"] = (1, 1), ["erfcinv"] = (1, 1),
            ["ulp"] = (1, 1), ["nfact"] = (1, 1), ["arccsc"] = (1, 1), ["lambw0"] = (1, 1), ["lambw1"] = (1, 1),
            ["sgngamma"] = (1, 1), ["loggamma"] = (1, 1), ["digamma"] = (1, 1),
            ["nck"] = (2, 2), ["npk"] = (2, 2), ["stirl2"] = (2, 2), ["euler"] = (2, 2), ["harm"] = (2, 2), ["ndig"] = (2, 2),
            ["dig10"] = (2, 2), ["factval"] = (2, 2), ["factexp"] = (2, 2), ["gammal"] = (2, 2), ["gammau"] = (2, 2),
            ["gammap"] = (2, 2), ["gammaregl"] = (2, 2), ["gammaq"] = (2, 2), ["gammaregu"] = (2, 2), ["beta"] = (2, 2),
            ["logbeta"] = (2, 2),
            ["puni"] = (3, 3), ["cuni"] = (3, 3), ["quni"] = (3, 3), ["pnor"] = (3, 3), ["cnor"] = (3, 3), ["qnor"] = (3, 3),
            ["dig"] = (3, 3), ["betainc"] = (3, 3), ["betai"] = (3, 3), ["betareg"] = (3, 3),
            ["confrac"] = (1, int.MaxValue), ["conpol"] = (0, int.MaxValue),
        };

        /// <summary>
        /// mXparser's rarer special functions. A NaN argument gives NaN (as in mXparser; only a NaN final result is an
        /// error).
        /// </summary>
        private static double Special(string name, List<double> a, string? display = null)
        {
            if (!SpecialArity.TryGetValue(name, out var arity)) throw Syntax($"unknown function '{display ?? name}'");
            Arity(display ?? name, a, arity.Min, arity.Max);
            if (a.Any(double.IsNaN)) return double.NaN;
            switch (name)
            {
                case "sa" or "sinc": return a[0] == 0 ? 1 : Math.Sin(Math.PI * a[0]) / (Math.PI * a[0]);
                case "sinc-unnormalized": return a[0] == 0 ? 1 : Math.Sin(a[0]) / a[0];
                case "bell": return S.Bell(NonNegative("Bell", a[0]));
                case "pi": return S.PrimeCount(a[0]);
                case "ei": return S.Ei(a[0]);
                case "li": return S.Li(a[0]);
                case "li-offset": return S.Li(a[0]) - S.Li(2);
                case "erf": return S.Erf(a[0]);
                case "erfc": return S.Erfc(a[0]);
                case "erfinv": return S.ErfInv(a[0]);
                case "erfcinv": return S.ErfcInv(a[0]);
                case "ulp": return Math.BitIncrement(Math.Abs(a[0])) - Math.Abs(a[0]);
                case "nfact": return S.Factorize(a[0]).Count;
                case "arccsc": return Math.Asin(1 / a[0]);
                case "lambw0": return S.LambertW(a[0], 0);
                case "lambw1": return S.LambertW(a[0], -1);
                case "sgngamma": return S.SignGamma(a[0]);
                case "loggamma": return S.LogGamma(a[0]);
                case "digamma": return S.Digamma(a[0]);

                case "nck": return Binomial(a[0], a[1]);
                case "npk":
                {
                    if (!IsInteger(a[1]) || a[1] < 0) throw new MathException("nPk needs a whole k of at least 0");
                    if (IsInteger(a[0]) && a[0] >= 0 && a[1] > a[0]) return 0;
                    double result = 1;
                    for (var i = 0; i < (int)Math.Min(a[1], 1000); i++) result *= a[0] - i;
                    return result;
                }
                case "stirl2": return S.Stirling2(NonNegative("Stirl2", a[0]), NonNegative("Stirl2", a[1]));
                case "euler": return S.Eulerian(NonNegative("Euler", a[0]), (int)Integer(a[1], "Euler"));
                case "harm":
                {
                    // Harm(x, n): the generalized harmonic number Σ 1/kˣ for k = 1..n.
                    double h = 0;
                    for (var k = NonNegative("Harm", a[1]); k >= 1; k--) h += Math.Pow(k, -a[0]);
                    return h;
                }
                case "ndig": return S.Digits(a[0], a[1]).Count;
                case "dig10": return Digit(a[0], a[1], 10);
                case "factval" or "factexp":
                {
                    var factors = S.Factorize(a[0]);
                    var id = Integer(a[1], name);
                    if (id < 1 || id > factors.Count)
                        throw new MathException($"{name}: the number has {factors.Count} distinct prime factor(s), not {id}");
                    return name == "factval" ? factors[(int)id - 1].Factor : factors[(int)id - 1].Exponent;
                }
                case "gammal": return S.GammaP(a[0], a[1]) * Gamma(a[0]);
                case "gammau": return S.GammaQ(a[0], a[1]) * Gamma(a[0]);
                case "gammap" or "gammaregl": return S.GammaP(a[0], a[1]);
                case "gammaq" or "gammaregu": return S.GammaQ(a[0], a[1]);
                case "beta":
                    return a[0] > 0 && a[1] > 0 ? Math.Exp(S.LogBeta(a[0], a[1])) : Gamma(a[0]) * Gamma(a[1]) / Gamma(a[0] + a[1]);
                case "logbeta": return S.LogBeta(a[0], a[1]);

                case "puni" or "cuni" or "quni":
                {
                    var (x, lo, hi) = (a[0], a[1], a[2]);
                    if (lo >= hi) throw new MathException($"{display ?? name} needs a lower bound below the upper bound");
                    if (name == "puni") return x >= lo && x <= hi ? 1 / (hi - lo) : 0;
                    if (name == "cuni") return x <= lo ? 0 : x >= hi ? 1 : (x - lo) / (hi - lo);
                    if (x is < 0 or > 1) throw new MathException("qUni needs a probability from 0 to 1");
                    return lo + x * (hi - lo);
                }
                case "pnor": return S.NormalPdf(a[0], a[1], a[2]);
                case "cnor": return S.NormalCdf(a[0], a[1], a[2]);
                case "qnor": return S.NormalQuantile(a[0], a[1], a[2]);
                case "dig": return Digit(a[0], a[1], a[2]);
                case "betai" or "betareg": return S.BetaRegularized(a[0], a[1], a[2]);
                case "betainc": return S.BetaRegularized(a[0], a[1], a[2]) * Math.Exp(S.LogBeta(a[1], a[2]));

                case "confrac":
                {
                    // a1 + 1/(a2 + 1/(a3 + …))
                    var value = a[^1];
                    for (var i = a.Count - 2; i >= 0; i--)
                    {
                        if (value == 0) throw new MathException("division by zero");
                        value = a[i] + 1 / value;
                    }
                    return value;
                }
                default:
                {
                    // conpol: the continuant K(a1, …, an) = an·K(a1, …, an-1) + K(a1, …, an-2), K() = 1
                    double previous = 1, current = a.Count > 0 ? a[0] : 1;
                    for (var i = 1; i < a.Count; i++) (previous, current) = (current, a[i] * current + previous);
                    return current;
                }
            }
        }

        /// <summary>
        /// The digit of |n| at a position in a base: 1 to n count from the left, 0 to -(n-1) from the right.
        /// </summary>
        private static double Digit(double n, double position, double numberBase)
        {
            var digits = S.Digits(n, numberBase);
            var p = Integer(position, "dig");
            var index = p >= 1 ? p - 1 : digits.Count - 1 + p;
            if (index < 0 || index >= digits.Count)
                throw new MathException($"the number has {digits.Count} digit(s); position {p} is outside it");
            return digits[(int)index];
        }
    }
}
