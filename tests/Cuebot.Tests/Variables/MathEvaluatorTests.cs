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
    [InlineData("log(8, 2)", 3)]
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
    [InlineData("1 & 2", "unexpected '&'")]
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
