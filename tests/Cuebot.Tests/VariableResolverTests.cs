using Cuebot.Core.Actions;

namespace Cuebot.Tests;

public class VariableResolverTests
{
    private static readonly Dictionary<string, object?> Args = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = "Alice",
        ["bits"] = 100,
        ["ratio"] = 1.5,
        ["isMod"] = true,
        ["empty"] = null,
    };

    [Theory]
    [InlineData("Hi %user%!", "Hi Alice!")]
    [InlineData("%USER% cheered %bits%", "Alice cheered 100")]
    [InlineData("%ratio% %isMod%", "1.5 true")]
    [InlineData("[%empty%]", "[]")]
    [InlineData("100% sure, %unknown% stays", "100% sure, %unknown% stays")]
    [InlineData("", "")]
    public void Resolves_arguments(string template, string expected) =>
        Assert.Equal(expected, VariableResolver.Resolve(template, Args));

    [Fact]
    public void Resolves_globals_after_arguments()
    {
        var globals = new Dictionary<string, string> { ["counter"] = "7" };
        var result = VariableResolver.Resolve("~counter~ for %user%, ~missing~", Args, globals.GetValueOrDefault);
        Assert.Equal("7 for Alice, ~missing~", result);
    }

    [Fact]
    public void Falls_back_to_case_insensitive_lookup_for_plain_dictionaries()
    {
        var plain = new Dictionary<string, object?> { ["UserName"] = "bob" };
        Assert.Equal("bob", VariableResolver.Resolve("%username%", plain));
    }
}

public class ConditionTests
{
    [Theory]
    [InlineData("10", "greater than", "9", true)]
    [InlineData("10", "greater than", "9.5", true)]
    [InlineData("abc", "greater than", "abd", false)]
    [InlineData("Hello", "equals", "hello", true)]
    [InlineData("5.0", "equals", "5", true)]
    [InlineData("hello world", "contains", "WORLD", true)]
    [InlineData("hello", "does not contain", "x", true)]
    [InlineData("!cmd x", "starts with", "!cmd", true)]
    [InlineData("  ", "is empty", "", true)]
    [InlineData("a", "is not empty", "", true)]
    [InlineData("abc123", "matches regex", @"^\w+\d+$", true)]
    public void Evaluates(string left, string op, string right, bool expected) =>
        Assert.Equal(expected, Conditions.Evaluate(left, op, right));

    [Fact]
    public void Unknown_operator_throws() =>
        Assert.Throws<ArgumentException>(() => Conditions.Evaluate("a", "nope", "b"));
}
