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
}
