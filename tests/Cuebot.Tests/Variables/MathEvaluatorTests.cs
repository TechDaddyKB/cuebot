using Cuebot.Core.Variables;

namespace Cuebot.Tests.Variables;

public class MathEvaluatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("100 / 10 / 5", 2)]
    [InlineData("7 % 3", 1)]
    [InlineData("-7 % 3", -1)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("2 ^ -1", 0.5)]
    [InlineData("--3", 3)]
    [InlineData("+4", 4)]
    [InlineData("-(2 + 3)", -5)]
    [InlineData(".5 + 1.25", 1.75)]
    [InlineData("1e3 + 2E-1", 1000.2)]
    [InlineData("3 > 2", 1)]
    [InlineData("3 < 2", 0)]
    [InlineData("2 >= 2 && 1 <= 0", 0)]
    [InlineData("1 == 1 || 0", 1)]
    [InlineData("5 = 5", 1)]
    [InlineData("5 != 5", 0)]
    [InlineData("5 <> 4", 1)]
    [InlineData("!0", 1)]
    [InlineData("!5", 0)]
    [InlineData("1 + 2 > 2", 1)]
    [InlineData("min(4, 2, 8)", 2)]
    [InlineData("max(4, 2, 8)", 8)]
    [InlineData("abs(-3)", 3)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("round(3.14159, 2)", 3.14)]
    [InlineData("floor(2.7) + ceil(2.1)", 5)]
    [InlineData("trunc(-2.7)", -2)]
    [InlineData("sign(-9)", -1)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("pow(2, 10)", 1024)]
    [InlineData("log(2, 8)", 3)]   // mXparser order: base first
    [InlineData("log10(1000)", 3)]
    [InlineData("ln(e)", 1)]
    [InlineData("exp(0)", 1)]
    [InlineData("clamp(15, 0, 10)", 10)]
    [InlineData("clamp(-5, 0, 10)", 0)]
    [InlineData("if(2 > 1, 10, 20)", 10)]
    [InlineData("MAX(1, 2)", 2)]
    [InlineData("  ( ( 1 ) )  ", 1)]
    [InlineData("true + true", 2)]
    public void Evaluates(string expression, double expected) =>
        Assert.Equal(expected, MathEvaluator.Evaluate(expression), 10);

    [Fact]
    public void Pi_constant() => Assert.Equal(Math.PI, MathEvaluator.Evaluate("pi"));

    /// <summary>
    /// mXparser 4.4's documented tokens (the list Streamer.bot links for $math()$), with the examples its token table
    /// gives where it gives one.
    /// </summary>
    [Theory]
    // operators
    [InlineData("10 # 3", 1)]
    [InlineData("5!", 120)]
    [InlineData("0!", 1)]
    [InlineData("(2+1)!", 6)]
    [InlineData("2^3!", 64)]
    [InlineData("3!^2", 36)]
    [InlineData("50%", 0.5)]
    [InlineData("50% * 200", 100)]
    [InlineData("200 * 10%", 20)]
    [InlineData("7 % 3", 1)]   // between two operands % stays Cuebot's modulo
    [InlineData("2^^3", 16)]
    [InlineData("3^^2", 27)]
    [InlineData("5! != 120", 0)]
    // boolean operators and relations
    [InlineData("1 & 0", 0)]
    [InlineData("1 | 0", 1)]
    [InlineData("1 /\\ 1", 1)]
    [InlineData("0 \\/ 1", 1)]
    [InlineData("1 ~& 1", 0)]
    [InlineData("1 ~&& 0", 1)]
    [InlineData("0 ~| 0", 1)]
    [InlineData("1 ~|| 0", 0)]
    [InlineData("1 (+) 1", 0)]
    [InlineData("1 (+) 0", 1)]
    [InlineData("1 --> 0", 0)]
    [InlineData("0 --> 0", 1)]
    [InlineData("0 <-- 1", 0)]
    [InlineData("1 -/> 0", 1)]
    [InlineData("0 </- 1", 1)]
    [InlineData("1 <-> 1", 1)]
    [InlineData("1 <-> 0", 0)]
    [InlineData("~0", 1)]
    [InlineData("~1", 0)]
    [InlineData("2 ~= 3", 1)]
    [InlineData("1 < -1", 0)]
    [InlineData("3 > 2 & 2 > 1", 1)]
    // bitwise
    [InlineData("10 @& 2", 2)]
    [InlineData("10 @^ 2", 8)]
    [InlineData("10 @| 5", 15)]
    [InlineData("10 @<< 2", 40)]
    [InlineData("10 @>> 2", 2)]
    [InlineData("@~10", -11)]
    // number literals
    [InlineData("b.10101", 21)]
    [InlineData("B.10101", 21)]
    [InlineData("b2.10010", 18)]
    [InlineData("o.1027", 535)]
    [InlineData("b8.1027", 535)]
    [InlineData("h.12fE", 4862)]
    [InlineData("b16.12fE", 4862)]
    [InlineData("b1.111", 3)]
    [InlineData("b36.z", 35)]
    [InlineData("1_2", 0.5)]
    [InlineData("2_3_4", 2.75)]
    // unary functions
    [InlineData("tg(0)", 0)]
    [InlineData("ctg(pi/4)", 1)]
    [InlineData("sec(0)", 1)]
    [InlineData("cosec(pi/2)", 1)]
    [InlineData("arcsin(1)", Math.PI / 2)]
    [InlineData("arctg(1)", Math.PI / 4)]
    [InlineData("acot(1)", Math.PI / 4)]
    [InlineData("tgh(0)", 0)]
    [InlineData("arsinh(0)", 0)]
    [InlineData("rad(180)", Math.PI)]
    [InlineData("deg(pi)", 180)]
    [InlineData("log2(8)", 3)]
    [InlineData("lg(100)", 2)]
    [InlineData("sgn(-3)", -1)]
    [InlineData("not(0)", 1)]
    [InlineData("Fib(10)", 55)]
    [InlineData("Luc(5)", 11)]
    [InlineData("harm(3)", 1.8333333333333333)]
    [InlineData("ispr(7)", 1)]
    [InlineData("ispr(8)", 0)]
    [InlineData("ndig10(12345)", 5)]
    [InlineData("Gamma(5)", 24)]
    [InlineData("Gamma(0.5)", 1.7724538509055159)]
    [InlineData("2.5!", 3.3233509704478426)]
    [InlineData("isNaN([NaN])", 1)]
    // binary and 3-argument functions
    [InlineData("mod(10, 3)", 1)]
    [InlineData("C(5, 2)", 10)]
    [InlineData("root(3, 27)", 3)]
    [InlineData("root(3, -8)", -2)]
    [InlineData("round(2.2, 0)", 2)]
    [InlineData("round(2.6, 0)", 3)]
    [InlineData("round(2.66, 1)", 2.7)]
    [InlineData("round(1250, -2)", 1300)]
    [InlineData("KDelta(2, 2)", 1)]
    [InlineData("if(1 > 2, 1, 2)", 2)]
    [InlineData("chi(1, 0, 1)", 0)]
    [InlineData("CHi(1, 0, 1)", 1)]
    [InlineData("Chi(0, 0, 1)", 1)]
    [InlineData("cHi(0, 0, 1)", 0)]
    // variadic functions
    [InlineData("iff(1 > 2, 10; 2 > 1, 20)", 20)]
    [InlineData("iff(0, 1, 1, 2)", 2)]
    [InlineData("gcd(12, 18)", 6)]
    [InlineData("lcm(4, 6)", 12)]
    [InlineData("add(1, 2, 3)", 6)]
    [InlineData("multi(2, 3, 4)", 24)]
    [InlineData("mean(1, 2, 3, 4)", 2.5)]
    [InlineData("var(1, 2, 3, 4)", 1.6666666666666667)]
    [InlineData("std(2, 4, 4, 4, 5, 5, 7, 9)", 2.1380899352993952)]
    [InlineData("med(3, 1, 2)", 2)]
    [InlineData("med(4, 1, 3, 2)", 2.5)]
    [InlineData("mode(1, 2, 2, 3)", 2)]
    [InlineData("ndist(1, 1, 2)", 2)]
    [InlineData("argmax(1, 5, 3)", 2)]
    [InlineData("argmin(4, 1, 3)", 2)]
    [InlineData("coalesce([NaN], 5)", 5)]
    [InlineData("and(1, 1, 0)", 0)]
    [InlineData("or(0, 0, 1)", 1)]
    [InlineData("xor(1, 1, 1)", 1)]
    [InlineData("base(2, 1, 0, 1)", 5)]
    // constants
    [InlineData("[true] + [false]", 1)]
    [InlineData("[phi]", 1.6180339887498949)]
    [InlineData("[gam]", 0.57721566490153286)]
    [InlineData("2*pi", 2 * Math.PI)]
    [InlineData("e*3", Math.E * 3)]
    public void Evaluates_mxparser_tokens(string expression, double expected) =>
        Assert.Equal(expected, MathEvaluator.Evaluate(expression), 10);

    [Fact]
    public void Mxparser_random_functions_and_variables_stay_in_range()
    {
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(MathEvaluator.Evaluate("rUni(2, 10)", rng), 2, 10);
            var d = MathEvaluator.Evaluate("rUnid(2, 100)", rng);
            Assert.InRange(d, 2, 100);
            Assert.Equal(Math.Truncate(d), d);
            Assert.Contains(MathEvaluator.Evaluate("rList(3, 7, 9)", rng), new double[] { 3, 7, 9 });
            Assert.InRange(MathEvaluator.Evaluate("[Int1]", rng), -10, 10);
            Assert.InRange(MathEvaluator.Evaluate("[nat2]", rng), 0, 100);
            Assert.InRange(MathEvaluator.Evaluate("[Nat3]", rng), 1, 1000);
            Assert.True(MathEvaluator.Evaluate("[Nat]", rng) >= 1);
            Assert.InRange(MathEvaluator.Evaluate("[Uni]", rng), 0, 1);
            Assert.True(double.IsFinite(MathEvaluator.Evaluate("3*rNor(0, 1) + [Nor]", rng)));
        }
    }

    [Theory]
    [InlineData("1 / 0", "division by zero")]
    [InlineData("5 % 0", "modulo by zero")]
    [InlineData("", "empty")]
    [InlineData("1 +", "unexpected end")]
    [InlineData("(1 + 2", "missing ')'")]
    [InlineData("1 + 2)", "unexpected ')'")]
    [InlineData("foo + 1", "unknown name 'foo'")]
    [InlineData("foo(1)", "unknown function 'foo'")]
    [InlineData("sqrt(-1)", "negative")]
    [InlineData("log(0)", "not positive")]
    [InlineData("abs(1, 2)", "abs expects 1 argument")]
    [InlineData("min()", "at least 1")]
    [InlineData("10 ^ 400", "not a finite number")]
    [InlineData("1 &&& 2", "unexpected '&'")]
    [InlineData("[foo] + 1", "unknown name '[foo]'")]
    [InlineData("b2.102", "not a base-2 number")]
    [InlineData("(-1)!", "factorial of a negative")]
    [InlineData("chI(1, 0, 2)", "use chi, CHi, Chi or cHi")]
    [InlineData("2^^0.5", "tetration")]
    [InlineData("hello world", "unknown name")]
    [InlineData("1..2", "not a number")]
    public void Reports_errors(string expression, string message)
    {
        var ex = Assert.Throws<MathException>(() => MathEvaluator.Evaluate(expression));
        Assert.Contains(message, ex.Message);
        Assert.False(MathEvaluator.TryEvaluate(expression, out _, out var error));
        Assert.Equal(ex.Message, error);
    }

    [Fact]
    public void Rejects_deep_nesting_and_long_input()
    {
        Assert.Throws<MathException>(() => MathEvaluator.Evaluate(new string('(', 200) + "1" + new string(')', 200)));
        Assert.Throws<MathException>(() => MathEvaluator.Evaluate(new string('-', 500) + "1"));
        Assert.Throws<MathException>(() => MathEvaluator.Evaluate(string.Join("+", Enumerable.Repeat("1", 3000))));
    }

    [Fact]
    public void Random_is_inclusive_integer_for_integer_bounds()
    {
        var rng = new Random(1);
        var seen = new HashSet<double>();
        for (var i = 0; i < 500; i++)
        {
            var v = MathEvaluator.Evaluate("random(1, 3)", rng);
            Assert.InRange(v, 1, 3);
            Assert.Equal(Math.Truncate(v), v);
            seen.Add(v);
        }
        Assert.Equal(new double[] { 1, 2, 3 }, seen.Order());

        var reversed = MathEvaluator.Evaluate("random(5, 5)", rng);
        Assert.Equal(5, reversed);
        var real = MathEvaluator.Evaluate("random(0.5, 1.5)", rng);
        Assert.InRange(real, 0.5, 1.5);
        Assert.InRange(MathEvaluator.Evaluate("random()", rng), 0, 1);
        Assert.Throws<MathException>(() => MathEvaluator.Evaluate("random(3)", rng));
    }

    [Theory]
    [InlineData(3.0, "3")]
    [InlineData(-0.0, "0")]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(2.5, "2.5")]
    [InlineData(1e20, "1E+20")]
    [InlineData(-42.0, "-42")]
    public void Formats_results(double value, string expected) => Assert.Equal(expected, MathEvaluator.FormatNumber(value));

    private static void AssertClose(double expected, string expression, double relative = 1e-12)
    {
        var actual = MathEvaluator.Evaluate(expression);
        var tolerance = relative * Math.Max(Math.Abs(expected), 1);
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{expression}: expected {expected:R}, got {actual:R}");
    }

    /// <summary>mXparser's iterated operators: sum, prod, avg, vari, stdi, mini, maxi (i, from, to, expression[, step]).</summary>
    [Theory]
    [InlineData("sum(i, 1, 10, i)", 55)]
    [InlineData("sum(i, 1, 10, i^2)", 385)]
    [InlineData("sum(i, 1, 10, i, 2)", 25)]
    [InlineData("sum(i, 10, 1, i)", 55)]
    [InlineData("sum(i, 0, 1, i, 0.25)", 2.5)]
    [InlineData("sum(i; 1; 3; 2*i)", 12)]
    [InlineData("prod(i, 1, 5, i)", 120)]
    [InlineData("avg(i, 1, 4, i)", 2.5)]
    [InlineData("vari(i, 1, 4, i)", 1.6666666666666667)]
    [InlineData("stdi(i, 1, 4, i)", 1.2909944487358056)]
    [InlineData("mini(i, 0, 4, (i-2)^2)", 0)]
    [InlineData("maxi(i, 1, 3, i^2)", 9)]
    [InlineData("sum(i, 1, 3, sum(j, 1, i, j))", 10)]
    [InlineData("sum(i, 1, 3, sum(i, 1, 2, i))", 9)]   // the inner index shadows the outer one
    [InlineData("sum(k, 1, 3, k*pi) / pi", 6)]
    [InlineData("sum(1, 2, 3, 4)", 10)]                 // Cuebot's variadic sum and avg still work
    [InlineData("avg(1, 2, 3)", 2)]
    [InlineData("sum(pi, 1, 2, 3) - pi", 6)]
    public void Evaluates_iterated_operators(string expression, double expected) => AssertClose(expected, expression);

    /// <summary>if and iff evaluate only the branch they return, as mXparser does.</summary>
    [Theory]
    [InlineData("if(1, 1, 1/0)", 1)]
    [InlineData("if(0, 1/0, 2)", 2)]
    [InlineData("iff(1, 5; 1, 1/0)", 5)]
    [InlineData("iff(0, 1/0; 1, 7)", 7)]
    [InlineData("sum(i, 0, 3, if(i > 0, 1/i, 0))", 1.8333333333333333)]
    [InlineData("if(1 > 2, 1, if(2 > 1, 3, 4))", 3)]
    public void If_and_iff_are_lazy(string expression, double expected) => AssertClose(expected, expression);

    /// <summary>mXparser's calculus operators: der, der-, der+, int, solve, diff, difb.</summary>
    [Theory]
    [InlineData("der(x^2, x, 3)", 6)]
    [InlineData("der(sin(x), x, 0)", 1)]
    [InlineData("der(exp(x), x, 2)", 7.38905609893065)]
    [InlineData("der(ln(x), x, 0.05)", 20)]
    [InlineData("der(x^3, x, -2)", 12)]
    [InlineData("der-(abs(x), x, 0)", -1)]
    [InlineData("der+(abs(x), x, 0)", 1)]
    [InlineData("der+(sqrt(x), x, 1)", 0.5)]
    [InlineData("sum(t, 1, 3, der(t*x^2, x, 1))", 12)]
    [InlineData("sum(x, 1, 3, der(x^2, x))", 12)]     // the point is the bound value of x
    public void Evaluates_derivatives(string expression, double expected) => AssertClose(expected, expression, 1e-8);

    [Theory]
    [InlineData("int(x^2, x, 0, 3)", 9)]
    [InlineData("int(sin(x), x, 0, pi)", 2)]
    [InlineData("int(sin(x), x, pi, 0)", -2)]
    [InlineData("int(1, x, 2, 2)", 0)]
    [InlineData("int(exp(-x^2), x, -10, 10)", 1.7724538509055159)]
    [InlineData("int(1/x, x, 1, e)", 1)]
    [InlineData("int(1/sqrt(x), x, 0, 1)", 2)]
    [InlineData("int(int(x*y, y, 0, 1), x, 0, 2)", 1)]
    [InlineData("solve(x^2 - 2, x, 0, 2)", 1.4142135623730951)]
    [InlineData("solve(cos(x) - x, x, 0, 1)", 0.7390851332151607)]
    [InlineData("solve(x^2 - 4, x, -5, 0)", -2)]
    [InlineData("solve(x^2 - 1, x, -2, 0.5)", -1)]   // no sign change between the bounds: searched inside
    [InlineData("solve(x - 3, x, 3, 5)", 3)]
    [InlineData("sum(x, 1, 3, diff(x^2, x))", 15)]
    [InlineData("sum(x, 1, 3, difb(x^2, x))", 9)]
    [InlineData("sum(x, 1, 3, diff(x^2, x, 0.5))", 6.75)]
    public void Evaluates_integrals_roots_and_differences(string expression, double expected) => AssertClose(expected, expression, 1e-9);

    /// <summary>mXparser's units, metric prefixes, physical and astronomical constants, in SI base units.</summary>
    [Theory]
    [InlineData("10*[km]/[h]", 2.7777777777777777)]
    [InlineData("[h]/[min]", 60)]
    [InlineData("[day]/[h]", 24)]
    [InlineData("[week]/[day]", 7)]
    [InlineData("[yearj]/[day]", 365.25)]
    [InlineData("[mile]/[km]", 1.609344)]
    [InlineData("[ft]/[inch]", 12)]
    [InlineData("[ha]/[are]", 100)]
    [InlineData("[l]/[ml]", 1000)]
    [InlineData("[kg]/[gr]", 1000)]
    [InlineData("[lb]/[oz]", 16)]
    [InlineData("[kB]/[b]", 8000)]
    [InlineData("[MB]/[kB]", 1000)]
    [InlineData("[GeV]/[MeV]", 1000)]
    [InlineData("180*[deg]", Math.PI)]
    [InlineData("60*[‘]/[deg]", 1)]
    [InlineData("[knot]*[h]/[nmi]", 1)]
    [InlineData("50*[%]", 0.5)]
    [InlineData("5*[k] + 2*[M]", 2005000)]
    [InlineData("[mic]*[M]", 1)]
    [InlineData("[c]", 299792458)]
    [InlineData("[c]*[s]/[km]", 299792.458)]
    [InlineData("[g]", 9.80665)]
    [InlineData("[G.]", 6.6743e-11)]
    [InlineData("[hP]/(2*pi) - [h-]", 0)]
    [InlineData("[ly]/([c]*[yearj])", 1)]
    [InlineData("[pc]/[au]", 206264.80624709636)]
    [InlineData("[kpc]/[pc]", 1000)]
    [InlineData("[Earth-R]/[km]", 6371)]
    [InlineData("[Moon-D]/[km]", 384400)]
    [InlineData("[Solar-M]", 1.98847e30)]
    [InlineData("[Kat]", 0.915965594177219)]
    [InlineData("[Dz3]", 1.2020569031595942)]
    [InlineData("[Om]*exp([Om])", 1)]
    [InlineData("[P2]", 2.295587149392638)]
    public void Evaluates_units_and_constants(string expression, double expected) => AssertClose(expected, expression, 1e-12);

    [Fact]
    public void Planetary_constants_are_plausible()
    {
        AssertClose(81.3, "[Earth-M]/[Moon-M]", 1e-3);
        AssertClose(1, "[Earth-D]/[au]", 1e-4);
        AssertClose(5.2, "[Jupiter-D]/[au]", 1e-3);
        AssertClose(11, "[Jupiter-R]/[Earth-R]", 0.03);
        AssertClose(333000, "[Solar-M]/[Earth-M]", 1e-3);
    }

    /// <summary>mXparser's rarer special functions, against reference values.</summary>
    [Theory]
    [InlineData("erf(0)", 0)]
    [InlineData("erf(0.5)", 0.5204998778130465)]
    [InlineData("erf(1)", 0.8427007929497149)]
    [InlineData("erf(-1)", -0.8427007929497149)]
    [InlineData("erf(3)", 0.9999779095030014)]
    [InlineData("erfc(-1)", 1.8427007929497148)]
    [InlineData("erfc(3)/1e-5", 2.209049699858544)]
    [InlineData("erfc(5)/1e-12", 1.5374597944280349)]
    [InlineData("erfInv(0.5)", 0.4769362762044699)]
    [InlineData("erfInv(erf(1.5))", 1.5)]
    [InlineData("erfInv(-0.9)", -1.1630871536766743)]
    [InlineData("erfcInv(erfc(4))", 4)]
    [InlineData("erfcInv(1.5)", -0.4769362762044699)]
    [InlineData("Ei(1)", 1.8951178163559368)]
    [InlineData("Ei(-1)", -0.21938393439552062)]
    [InlineData("Ei(-5)", -0.0011482955912753257)]
    [InlineData("Ei(50)/1e20", 1.0585636897131690)]
    [InlineData("li(2)", 1.0451637801174928)]
    [InlineData("li(10)", 6.1655995047872979)]
    [InlineData("li(2) - [li2]", 0)]
    [InlineData("Li(10)", 5.1204357246698051)]
    [InlineData("Bell(5)", 52)]
    [InlineData("Bell(10)", 115975)]
    [InlineData("Pi(100)", 25)]
    [InlineData("Pi(1000)", 168)]
    [InlineData("LambW0(1)", 0.5671432904097838)]
    [InlineData("LambW0(e)", 1)]
    [InlineData("LambW0(-0.1)", -0.11183255915896297)]
    [InlineData("LambW0(100)", 3.385630140290050)]
    [InlineData("LambW1(-0.1)", -3.577152063957297)]
    [InlineData("LambW1(-0.3)", -1.7813370234216276)]
    [InlineData("logGamma(10)", 12.801827480081469)]
    [InlineData("logGamma(0.5)", 0.5723649429247001)]
    [InlineData("sgnGamma(2.5) + sgnGamma(-0.5) + sgnGamma(-1.5)", 1)]
    [InlineData("diGamma(1)", -0.5772156649015329)]
    [InlineData("diGamma(0.5)", -1.9635100260214235)]
    [InlineData("diGamma(-0.5)", 0.03648997397857652)]
    [InlineData("GammaP(1, 1)", 0.6321205588285577)]
    [InlineData("GammaRegU(1, 1)", 0.36787944117144233)]
    [InlineData("GammaL(2, 1)", 0.26424111765711533)]
    [InlineData("GammaU(2, 1)", 0.7357588823428847)]
    [InlineData("GammaP(3, 10)", 0.9972306042844884)]
    [InlineData("GammaQ(3, 10) + GammaRegL(3, 10)", 1)]
    [InlineData("Beta(2, 3)", 0.08333333333333333)]
    [InlineData("logBeta(2, 3)", -2.4849066497880004)]
    [InlineData("BetaI(0.5, 2, 3)", 0.6875)]
    [InlineData("BetaReg(0.2, 1, 1)", 0.2)]
    [InlineData("BetaInc(0.5, 2, 3)", 0.057291666666666664)]
    [InlineData("pNor(0, 0, 1)", 0.3989422804014327)]
    [InlineData("cNor(1.96, 0, 1)", 0.9750021048517795)]
    [InlineData("cNor(0, 5, 2)", 0.006209665325776132)]
    [InlineData("qNor(0.975, 0, 1)", 1.959963984540054)]
    [InlineData("qNor(cNor(-3, 0, 1), 0, 1)", -3)]
    [InlineData("pUni(0.5, 0, 2)", 0.5)]
    [InlineData("cUni(0.5, 0, 2)", 0.25)]
    [InlineData("qUni(0.25, 0, 2)", 0.5)]
    [InlineData("Sa(0)", 1)]
    [InlineData("sinc(0.5)", 0.6366197723675814)]
    [InlineData("Sinc(pi/2)", 0.6366197723675814)]
    [InlineData("ulp(1)", 2.220446049250313e-16)]
    [InlineData("arccsc(2)", Math.PI / 6)]
    [InlineData("nfact(360)", 3)]
    [InlineData("factval(360, 3)", 5)]
    [InlineData("factexp(360, 1)", 3)]
    [InlineData("ndig(255, 16)", 2)]
    [InlineData("ndig(0, 10)", 1)]
    [InlineData("dig(255, 1, 16)", 15)]
    [InlineData("dig(6, 1, 2)", 1)]
    [InlineData("dig10(12345, 1)", 1)]
    [InlineData("dig10(12345, 0)", 5)]
    [InlineData("dig10(12345, -1)", 4)]
    [InlineData("nCk(5, 2)", 10)]
    [InlineData("nPk(5, 2)", 20)]
    [InlineData("nPk(3, 5)", 0)]
    [InlineData("Stirl2(5, 2)", 15)]
    [InlineData("Stirl2(4, 4)", 1)]
    [InlineData("Euler(4, 1)", 11)]
    [InlineData("Euler(5, 2)", 66)]
    [InlineData("Harm(2, 3)", 1.3611111111111112)]
    [InlineData("harm(3)", 1.8333333333333333)]
    [InlineData("ConFrac(3, 7, 15, 1)", 355.0 / 113)]
    [InlineData("ConFrac(1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2)", 1.4142135623730951)]
    [InlineData("ConPol(1, 2, 3)", 10)]
    [InlineData("ConPol()", 1)]
    [InlineData("coalesce(erf([NaN]), 4)", 4)]   // a NaN argument gives NaN, as in mXparser
    public void Evaluates_special_functions(string expression, double expected) => AssertClose(expected, expression, 1e-12);

    [Theory]
    [InlineData("sum(i, 1, 3)", "unknown name 'i'")]     // not the iterated form: Cuebot's variadic sum
    [InlineData("prod(1, 2, 3)", "prod expects 4 to 5")]
    [InlineData("sum(i, 1, 2, i, 1, 1)", "unknown name 'i'")]
    [InlineData("prod(2, 1, 3, 4)", "first argument is the index")]
    [InlineData("sum(i, 1, 3, i, 0)", "step other than 0")]
    [InlineData("sum(i, 1, 1e6, i)", "more than 100000 steps")]
    [InlineData("sum(i, 1, 1000, sum(j, 1, 1000, j))", "more than 100000 evaluations")]
    [InlineData("sum(i, 1, 2, i $)", "unexpected '$' at position 16")]
    [InlineData("sum(i, 1, 2, j)", "unknown name 'j'")]
    [InlineData("sum(i, 1, 2, i", "missing ')'")]
    [InlineData("if(1, 2, foo)", "unknown name 'foo'")]   // the unused branch is still checked for syntax
    [InlineData("iff(0, 1; 1, 2; 1, bar(1))", "unknown function 'bar'")]
    [InlineData("iff(0, 1)", "not a finite number")]
    [InlineData("der(x^2, x)", "needs the point")]
    [InlineData("der(x^2, 2, 1)", "second argument is the variable")]
    [InlineData("der(ln(x), x, 0)", "not positive")]
    [InlineData("int(x, 1, 0, 1)", "second argument is the variable")]
    [InlineData("int(x, x, 0)", "int expects 4 argument(s)")]
    [InlineData("solve(x^2 + 1, x, -1, 1)", "no root")]
    [InlineData("diff(x^2, x)", "needs x to have a value")]
    [InlineData("dern(1, 2, 3)", "unknown function 'dern'")]
    [InlineData("erfInv(2)", "-1 to 1")]
    [InlineData("erfInv(1)", "not a finite number")]
    [InlineData("Ei(0)", "undefined at 0")]
    [InlineData("li(-1)", "at least 0")]
    [InlineData("li(1)", "undefined at 1")]
    [InlineData("Bell(-1)", "Bell needs a whole number")]
    [InlineData("LambW0(-1)", "at least -1/e")]
    [InlineData("LambW1(1)", "LambW1 needs a value")]
    [InlineData("logGamma(-2)", "undefined")]
    [InlineData("GammaP(0, 1)", "s greater than 0")]
    [InlineData("BetaI(2, 1, 1)", "x from 0 to 1")]
    [InlineData("qNor(1, 0, 1)", "between 0 and 1")]
    [InlineData("pNor(0, 0, 0)", "standard deviation")]
    [InlineData("factval(12, 3)", "2 distinct prime factor(s), not 3")]
    [InlineData("dig10(123, 4)", "position 4 is outside it")]
    [InlineData("Pi(1e9)", "up to 10000000")]
    [InlineData("Sinc(1, 2)", "Sinc expects 1 argument")]
    [InlineData("[foo]", "unknown name '[foo]'")]
    [InlineData("[KM]", "unknown name '[KM]'")]   // unit names are case-sensitive
    public void Reports_operator_and_special_function_errors(string expression, string message)
    {
        var ex = Assert.Throws<MathException>(() => MathEvaluator.Evaluate(expression));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Syntax_errors_are_marked()
    {
        Assert.True(Assert.Throws<MathException>(() => MathEvaluator.Evaluate("1 +")).IsSyntaxError);
        Assert.True(Assert.Throws<MathException>(() => MathEvaluator.Evaluate("foo(1)")).IsSyntaxError);
        Assert.False(Assert.Throws<MathException>(() => MathEvaluator.Evaluate("1/0")).IsSyntaxError);
        Assert.False(Assert.Throws<MathException>(() => MathEvaluator.Evaluate("sqrt(-1)")).IsSyntaxError);
    }

    [Fact]
    public void Random_functions_are_drawn_per_iteration()
    {
        var rng = new Random(3);
        var total = MathEvaluator.Evaluate("sum(i, 1, 100, rUnid(1, 6))", rng);
        Assert.InRange(total, 100, 600);
        Assert.NotEqual(0, total % 100);   // not the same roll every time
    }
}
