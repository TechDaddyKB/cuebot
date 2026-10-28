namespace Cuebot.Core.Variables;

/// <summary>
/// The special functions behind <see cref="MathEvaluator"/>: error function, exponential and logarithmic integrals,
/// Lambert W, log-gamma and digamma, incomplete gamma and beta functions, normal distribution, Bell numbers and the
/// like. Standard series, continued fractions and Newton/Halley refinements, accurate to about 1e-14 relative.
/// Invalid arguments throw <see cref="MathException"/>.
/// </summary>
internal static class MathSpecialFunctions
{
    private const double EulerGamma = 0.57721566490153286060651209008240243;
    private const double Epsilon = 1e-16;
    private const double Tiny = 1e-300;
    private static readonly double TwoOverSqrtPi = 2 / Math.Sqrt(Math.PI);

    // ---------------------------------------------------------------- error function

    public static double Erf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        var ax = Math.Abs(x);
        if (ax >= 2.5) return Math.Sign(x) * (1 - ErfcContinuedFraction(ax));
        // erf(x) = 2/√π e^(-x²) Σ 2ⁿ x^(2n+1) / (1·3·…·(2n+1)): every term is positive.
        double term = x, sum = x;
        for (var n = 1; n < 200 && Math.Abs(term) > Epsilon * Math.Abs(sum); n++)
        {
            term *= 2 * x * x / (2 * n + 1);
            sum += term;
        }
        return TwoOverSqrtPi * Math.Exp(-x * x) * sum;
    }

    public static double Erfc(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        return x >= 2.5 ? ErfcContinuedFraction(x) : 1 - Erf(x);
    }

    /// <summary>erfc(x) for x ≥ 2.5: e^(-x²)/√π · 1/(x + ½/(x + 1/(x + 3/2/(x + …)))).</summary>
    private static double ErfcContinuedFraction(double x)
    {
        var t = x;
        for (var n = 120; n >= 1; n--) t = x + n / 2.0 / t;
        return Math.Exp(-x * x) / Math.Sqrt(Math.PI) / t;
    }

    public static double ErfInv(double y)
    {
        if (y is < -1 or > 1) throw new MathException("erfInv needs a value from -1 to 1");
        if (y == 0) return 0;
        if (Math.Abs(y) > 0.5) return Math.Sign(y) * ErfcInv(1 - Math.Abs(y));
        var x = InitialInverse(y, 1 - y * y);
        for (var i = 0; i < 50; i++)
        {
            var f = Erf(x) - y;
            var step = f / (TwoOverSqrtPi * Math.Exp(-x * x) + x * f);   // Halley: f'' = -2x f'
            x -= step;
            if (Math.Abs(step) <= 1e-16 * Math.Abs(x)) break;
        }
        return x;
    }

    public static double ErfcInv(double z)
    {
        if (z is < 0 or > 2) throw new MathException("erfcInv needs a value from 0 to 2");
        if (z == 0) return double.PositiveInfinity;
        if (z == 2) return double.NegativeInfinity;
        if (z > 1) return -ErfcInv(2 - z);
        if (z > 0.5) return ErfInv(1 - z);
        var x = InitialInverse(1 - z, z * (2 - z));
        for (var i = 0; i < 50; i++)
        {
            var f = Erfc(x) - z;
            var step = -f / (TwoOverSqrtPi * Math.Exp(-x * x) - x * f);
            x -= step;
            if (Math.Abs(step) <= 1e-16 * Math.Abs(x)) break;
        }
        return x;
    }

    /// <summary>Winitzki's approximation of erfInv(y), given 1 - y² computed without cancellation.</summary>
    private static double InitialInverse(double y, double oneMinusYSquared)
    {
        const double a = 0.147;
        var ln = Math.Log(oneMinusYSquared);
        var t = 2 / (Math.PI * a) + ln / 2;
        return Math.Sign(y) * Math.Sqrt(Math.Sqrt(t * t - ln / a) - t);
    }

    // ---------------------------------------------------------------- exponential and logarithmic integrals

    public static double Ei(double x)
    {
        if (x == 0) throw new MathException("Ei is undefined at 0");
        if (x < 0) return -E1(-x);
        if (x < 40)
        {
            // γ + ln x + Σ xᵏ / (k·k!)
            double term = 1, sum = 0;
            for (var k = 1; k < 500; k++)
            {
                term *= x / k;
                var add = term / k;
                sum += add;
                if (add < Epsilon * sum) break;
            }
            return EulerGamma + Math.Log(x) + sum;
        }
        // Asymptotic: eˣ/x Σ k!/xᵏ, stopping at the smallest term.
        double t = 1, s = 1;
        for (var k = 1; k < 100; k++)
        {
            var next = t * k / x;
            if (next > t) break;
            t = next;
            s += t;
            if (t < Epsilon * s) break;
        }
        return Math.Exp(x) / x * s;
    }

    /// <summary>E₁(x) for x &gt; 0: the power series up to 1, a continued fraction (modified Lentz) above.</summary>
    private static double E1(double x)
    {
        if (x <= 1)
        {
            double term = 1, sum = 0;
            for (var k = 1; k < 200; k++)
            {
                term *= -x / k;
                var add = term / k;
                sum += add;
                if (Math.Abs(add) < Epsilon * Math.Abs(sum)) break;
            }
            return -EulerGamma - Math.Log(x) - sum;
        }
        var b = x + 1;
        var c = 1 / Tiny;
        var d = 1 / b;
        var h = d;
        for (var i = 1; i < 500; i++)
        {
            var an = -(double)i * i;
            b += 2;
            d = 1 / (an * d + b);
            c = b + an / c;
            var delta = c * d;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon) break;
        }
        return h * Math.Exp(-x);
    }

    public static double Li(double x)
    {
        if (x < 0) throw new MathException("li needs a number of at least 0");
        if (x == 0) return 0;
        if (x == 1) throw new MathException("li is undefined at 1");
        return Ei(Math.Log(x));
    }

    // ---------------------------------------------------------------- gamma family

    /// <summary>ln|Γ(x)|.</summary>
    public static double LogGamma(double x)
    {
        if (x <= 0 && x == Math.Floor(x)) throw new MathException("logGamma is undefined at 0 and negative whole numbers");
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        double shift = 0;
        while (x < 10) shift += Math.Log(x++);
        var z2 = 1 / (x * x);
        var series = (1.0 / 12 - z2 * (1.0 / 360 - z2 * (1.0 / 1260 - z2 * (1.0 / 1680 - z2 * (1.0 / 1188 - z2 * (691.0 / 360360 - z2 / 156)))))) / x;
        return (x - 0.5) * Math.Log(x) - x + 0.5 * Math.Log(2 * Math.PI) + series - shift;
    }

    public static double SignGamma(double x)
    {
        if (x <= 0 && x == Math.Floor(x)) throw new MathException("sgnGamma is undefined at 0 and negative whole numbers");
        return x > 0 || Math.Floor(x) % 2 == 0 ? 1 : -1;
    }

    /// <summary>ψ(x), the logarithmic derivative of Γ.</summary>
    public static double Digamma(double x)
    {
        if (x <= 0 && x == Math.Floor(x)) throw new MathException("diGamma is undefined at 0 and negative whole numbers");
        if (x < 0) return Digamma(1 - x) - Math.PI / Math.Tan(Math.PI * x);
        double shift = 0;
        while (x < 10) shift -= 1 / x++;
        var z2 = 1 / (x * x);
        var series = z2 * (1.0 / 12 - z2 * (1.0 / 120 - z2 * (1.0 / 252 - z2 * (1.0 / 240 - z2 * (1.0 / 132 - z2 * (691.0 / 32760 - z2 / 12))))));
        return Math.Log(x) - 0.5 / x - series + shift;
    }

    /// <summary>The regularized lower incomplete gamma function P(s, x).</summary>
    public static double GammaP(double s, double x)
    {
        CheckIncompleteGamma(s, x);
        if (x == 0) return 0;
        return x < s + 1 ? GammaSeries(s, x) : 1 - GammaContinuedFraction(s, x);
    }

    /// <summary>The regularized upper incomplete gamma function Q(s, x).</summary>
    public static double GammaQ(double s, double x)
    {
        CheckIncompleteGamma(s, x);
        if (x == 0) return 1;
        return x < s + 1 ? 1 - GammaSeries(s, x) : GammaContinuedFraction(s, x);
    }

    private static void CheckIncompleteGamma(double s, double x)
    {
        if (s <= 0) throw new MathException("incomplete gamma functions need s greater than 0");
        if (x < 0) throw new MathException("incomplete gamma functions need x of at least 0");
    }

    private static double GammaSeries(double s, double x)
    {
        double ap = s, del = 1 / s, sum = del;
        for (var n = 0; n < 10_000; n++)
        {
            ap++;
            del *= x / ap;
            sum += del;
            if (Math.Abs(del) < Math.Abs(sum) * Epsilon) break;
        }
        return sum * Math.Exp(-x + s * Math.Log(x) - LogGamma(s));
    }

    private static double GammaContinuedFraction(double s, double x)
    {
        var b = x + 1 - s;
        var c = 1 / Tiny;
        var d = 1 / b;
        var h = d;
        for (var i = 1; i < 10_000; i++)
        {
            var an = -i * (i - s);
            b += 2;
            d = an * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + an / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon) break;
        }
        return Math.Exp(-x + s * Math.Log(x) - LogGamma(s)) * h;
    }

    public static double LogBeta(double x, double y)
    {
        if (x <= 0 || y <= 0) throw new MathException("logBeta needs positive arguments");
        return LogGamma(x) + LogGamma(y) - LogGamma(x + y);
    }

    /// <summary>The regularized incomplete beta function I(x; a, b).</summary>
    public static double BetaRegularized(double x, double a, double b)
    {
        if (x is < 0 or > 1) throw new MathException("incomplete beta functions need x from 0 to 1");
        if (a <= 0 || b <= 0) throw new MathException("incomplete beta functions need positive a and b");
        if (x == 0) return 0;
        if (x == 1) return 1;
        var front = Math.Exp(a * Math.Log(x) + b * Math.Log(1 - x) - LogBeta(a, b));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(x, a, b) / a
            : 1 - front * BetaContinuedFraction(1 - x, b, a) / b;
    }

    private static double BetaContinuedFraction(double x, double a, double b)
    {
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1 / d;
        var h = d;
        for (var m = 1; m < 10_000; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon) break;
        }
        return h;
    }

    // ---------------------------------------------------------------- Lambert W

    /// <summary>Lambert W: branch 0 (x ≥ -1/e) or branch -1 (-1/e ≤ x &lt; 0), refined with Halley's method.</summary>
    public static double LambertW(double x, int branch)
    {
        const double minusOneOverE = -0.36787944117144232159552377016146;
        if (x < minusOneOverE - 1e-16) throw new MathException("Lambert W needs a value of at least -1/e");
        if (branch == -1 && x >= 0) throw new MathException("LambW1 needs a value from -1/e to 0 (excluded)");
        if (Math.Abs(x - minusOneOverE) <= 1e-16) return -1;
        if (x == 0) return 0;
        var p = Math.Sqrt(2 * (Math.E * x + 1));
        double w;
        if (branch == 0)
            w = x < -0.32 ? -1 + p - p * p / 3 + 11.0 / 72 * p * p * p : x < 2 ? Math.Log(1 + x) : Math.Log(x) - Math.Log(Math.Log(x));
        else
            w = x < -0.25 ? -1 - p - p * p / 3 - 11.0 / 72 * p * p * p : Math.Log(-x) - Math.Log(-Math.Log(-x));
        for (var i = 0; i < 100; i++)
        {
            var ew = Math.Exp(w);
            var f = w * ew - x;
            var next = w - f / (ew * (w + 1) - (w + 2) * f / (2 * w + 2));
            if (!double.IsFinite(next)) break;
            var done = Math.Abs(next - w) <= 1e-15 * (1 + Math.Abs(next));
            w = next;
            if (done) break;
        }
        return w;
    }

    // ---------------------------------------------------------------- distributions

    public static double NormalPdf(double x, double mean, double sd)
    {
        if (sd <= 0) throw new MathException("the standard deviation must be greater than 0");
        var z = (x - mean) / sd;
        return Math.Exp(-z * z / 2) / (sd * Math.Sqrt(2 * Math.PI));
    }

    public static double NormalCdf(double x, double mean, double sd)
    {
        if (sd <= 0) throw new MathException("the standard deviation must be greater than 0");
        return 0.5 * Erfc(-(x - mean) / (sd * Math.Sqrt(2)));
    }

    public static double NormalQuantile(double q, double mean, double sd)
    {
        if (sd <= 0) throw new MathException("the standard deviation must be greater than 0");
        if (q is <= 0 or >= 1) throw new MathException("qNor needs a probability between 0 and 1 (excluded)");
        return mean - sd * Math.Sqrt(2) * ErfcInv(2 * q);
    }

    // ---------------------------------------------------------------- numbers

    /// <summary>The n-th Bell number, from the Bell triangle.</summary>
    public static double Bell(int n)
    {
        var row = new double[n + 1];
        row[0] = 1;
        for (var i = 1; i <= n; i++)
        {
            var next = new double[n + 1];
            next[0] = row[i - 1];
            for (var j = 1; j <= i; j++) next[j] = next[j - 1] + row[j - 1];
            row = next;
        }
        return row[0];
    }

    /// <summary>π(n): the number of primes up to n (sieve, n up to 10 million).</summary>
    public static double PrimeCount(double n)
    {
        if (n < 2) return 0;
        if (n > 10_000_000) throw new MathException("Pi(n) supports n up to 10000000");
        var limit = (int)Math.Floor(n);
        var composite = new bool[limit + 1];
        var count = 0;
        for (var i = 2; i <= limit; i++)
        {
            if (composite[i]) continue;
            count++;
            for (var j = (long)i * i; j <= limit; j += i) composite[j] = true;
        }
        return count;
    }

    /// <summary>The distinct prime factors of |n| in ascending order, with their exponents.</summary>
    public static List<(long Factor, int Exponent)> Factorize(double n)
    {
        if (n != Math.Truncate(n) || Math.Abs(n) >= 1e15) throw new MathException("prime factorization needs a whole number");
        var m = (long)Math.Abs(n);
        var factors = new List<(long, int)>();
        for (long d = 2; d * d <= m; d += d == 2 ? 1 : 2)
        {
            var e = 0;
            while (m % d == 0)
            {
                m /= d;
                e++;
            }
            if (e > 0) factors.Add((d, e));
        }
        if (m > 1) factors.Add((m, 1));
        return factors;
    }

    /// <summary>The digits of |trunc(n)| in the given base, most significant first.</summary>
    public static List<int> Digits(double n, double numberBase)
    {
        if (numberBase != Math.Truncate(numberBase) || numberBase is < 1 or > 36) throw new MathException("the base must be a whole number from 1 to 36");
        var value = Math.Truncate(Math.Abs(n));
        if (value >= 1e15) throw new MathException("the number is too large for digit functions");
        var b = (long)numberBase;
        var v = (long)value;
        if (b == 1) return v > 10_000 ? throw new MathException("the number is too large for base 1") : Enumerable.Repeat(1, (int)v).ToList();
        var digits = new List<int>();
        do
        {
            digits.Add((int)(v % b));
            v /= b;
        }
        while (v > 0);
        digits.Reverse();
        return digits;
    }

    /// <summary>Stirling numbers of the second kind S(n, k).</summary>
    public static double Stirling2(int n, int k)
    {
        if (k > n) return 0;
        var row = new double[k + 1];
        row[0] = 1;   // S(0, 0)
        for (var i = 1; i <= n; i++)
            for (var j = Math.Min(i, k); j >= 0; j--)
                row[j] = j == 0 ? 0 : j * row[j] + row[j - 1];
        return row[k];
    }

    /// <summary>Eulerian numbers A(n, k): permutations of n elements with k ascents.</summary>
    public static double Eulerian(int n, int k)
    {
        if (k < 0 || (n > 0 && k >= n) || (n == 0 && k > 0)) return 0;
        var row = new double[n + 1];
        row[0] = 1;   // A(0, 0)
        for (var i = 1; i <= n; i++)
            for (var j = i - 1; j >= 0; j--)
                row[j] = (j + 1) * row[j] + (j > 0 ? (i - j) * row[j - 1] : 0);
        return row[k];
    }
}
