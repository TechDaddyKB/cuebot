using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Variables;

namespace Cuebot.Tests.Variables;

public class TextResolutionTests
{
    private readonly AppLog _log = new();
    private readonly TextEnvironment _env;
    private readonly Dictionary<string, object?> _args = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = "Alice",
        ["amount"] = 5.5m,
        ["amountText"] = "1234.5",
        ["n"] = 1234567,
        ["count"] = 3,
        ["i"] = 2,
        ["line1"] = "first",
        ["line2"] = "second",
        ["message"] = "  Hello, World  ",
        ["evil"] = "%user% $math(1+1)$ ~color~",
        ["when"] = "2026-01-02T03:04:05Z",
        ["date"] = "overridden",
        ["flag"] = true,
        ["emoji"] = "a👍b",
    };
    private readonly Dictionary<string, string> _globals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["color"] = "blue",
        ["score"] = "42",
        ["line3"] = "global third",
    };

    public TextResolutionTests() => _env = TestEnv.Create(_log);

    private string R(string template) => VariableResolver.Resolve(template, _args, _globals.GetValueOrDefault, _env);

    [Theory]
    [InlineData("%amount:c2%", "$5.50")]
    [InlineData("%amount:F3%", "5.500")]
    [InlineData("%n:N0%", "1,234,567")]
    [InlineData("%n:N2%", "1,234,567.00")]
    [InlineData("%count:D3%", "003")]
    [InlineData("%count:X%", "3")]
    [InlineData("%amountText:N1%", "1,234.5")]
    [InlineData("%amountText:c1%", "$1,234.5")]
    [InlineData("%user:N2%", "Alice")]
    [InlineData("%flag:N2%", "true")]
    [InlineData("%amount:0.0%", "5.5")]
    [InlineData("%amount:N2|de-DE%", "5,50")]
    [InlineData("%when:yyyy-MM-dd%", "2026-01-02")]
    [InlineData("~score:N1~", "42.0")]
    [InlineData("~color:N1~", "blue")]
    public void Applies_format_specifiers(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Culture_can_be_changed_on_the_environment()
    {
        _env.Culture = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("5,50", R("%amount:F2%"));
        Assert.Equal("5.5", R("%amount%")); // unformatted numbers stay invariant
    }

    [Fact]
    public void Invalid_format_leaves_marker_and_logs_once()
    {
        Assert.Equal("%amount:X% %amount:X%", R("%amount:X% %amount:X%"));
        Assert.Equal("%amount:X%", R("%amount:X%"));
        Assert.Single(_log.Recent(), e => e.Message.Contains("Invalid format 'X'"));
    }

    [Fact]
    public void Unknown_culture_leaves_marker() => Assert.Equal("%amount:c2|zz-QQQQ%", R("%amount:c2|zz-QQQQ%"));

    [Fact]
    public void Built_in_variables_use_the_environment_clock()
    {
        _args.Remove("date");
        Assert.Equal("03/04/2026", R("%date%"));
        Assert.Equal("2026-03-04", R("%date:yyyy-MM-dd%"));
        Assert.Equal("05:06", R("%time%"));
        Assert.Equal("5:06 AM", R("%time:h:mm tt%"));
        Assert.Equal("05:06", R("%time:t%"));
        Assert.Equal("05:06:07", R("%longtime%"));
        Assert.Equal("1772600767", R("%unixtime%"));
        Assert.Equal("1772600767000", R("%unixtimems%"));
        Assert.Equal("20260304_050607", R("%filedatetime%"));
        Assert.Equal("03/04/2026 05:06", R("%datetime%"));
        Assert.Equal("a\nb\tc\r\n", R("a%lf%b%tab%c%crlf%"));
        Assert.Equal(36, R("%guid%").Length);
    }

    [Fact]
    public void Arguments_win_over_built_ins() => Assert.Equal("overridden", R("%date%"));

    [Fact]
    public void Random_built_ins_respect_ranges()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(int.Parse(R("%random%"), CultureInfo.InvariantCulture), 1, 100);
            Assert.InRange(int.Parse(R("%random:3-5%"), CultureInfo.InvariantCulture), 3, 5);
            Assert.InRange(int.Parse(R("%random:-5-5%"), CultureInfo.InvariantCulture), -5, 5);
            Assert.InRange(int.Parse(R("%random:10..8%"), CultureInfo.InvariantCulture), 8, 10);
            Assert.InRange(double.Parse(R("%randomdecimal%"), CultureInfo.InvariantCulture), 0, 1);
        }
        Assert.Equal("%random:abc%", R("%random:abc%"));
    }

    [Theory]
    [InlineData("$math(1 + 2 * 3)$", "7")]
    [InlineData("$math(%count% * 2 + ~score~)$", "48")]
    [InlineData("$math(%amount% * 2)$", "11")]
    [InlineData("$math(min(%count%, 10))$", "3")]
    [InlineData("$math(10 % 4)$", "2")]
    [InlineData("$math(10%4)$", "2")]
    [InlineData("$math(round(%amount% / 3, 2))$", "1.83")]
    [InlineData("$math(%count% > 2)$", "1")]
    [InlineData("Total: $math(%count%+1)$ items", "Total: 4 items")]
    [InlineData("$length(%user%)$", "5")]
    [InlineData("$length(%emoji%)$", "3")]
    [InlineData("$length()$", "0")]
    [InlineData("$length(a, b)$", "4")]
    [InlineData("$upper(%user%)$ $lower(%user%)$", "ALICE alice")]
    [InlineData("[$trim(%message%)$]", "[Hello, World]")]
    [InlineData("$replace(%user%,A,a)$", "alice")]
    [InlineData("$replace(a-b-c,-, + )$", "a + b + c")]
    [InlineData("$replace(a\\,b,\\,,;)$", "a;b")]
    [InlineData("$replace(%message%, ,_)$", "__Hello,_World__")]
    [InlineData("$substring(%user%,1,3)$", "lic")]
    [InlineData("$substring(%user%,2)$", "ice")]
    [InlineData("$substring(%user%,-3)$", "ice")]
    [InlineData("$substring(%user%,10)$", "")]
    [InlineData("$substring(%user%,1,100)$", "lice")]
    [InlineData("$substring(%emoji%,1,1)$", "👍")]
    [InlineData("$urlencode(a b&c)$", "a%20b%26c")]
    [InlineData("$MATH(2*2)$", "4")]
    public void Evaluates_inline_functions(string template, string expected) => Assert.Equal(expected, R(template));

    [Theory]
    [InlineData("$upper($lower(MiXeD)$)$", "MIXED")]
    [InlineData("$math($length(%user%)$ * 2)$", "10")]
    [InlineData("$length($replace(%user%,ice,)$)$", "2")]
    [InlineData("$upper(f(x))$", "F(X)")]
    [InlineData("$length(:))$", "2")]
    [InlineData("$math((1 + 2) * (3 + 4))$", "21")]
    public void Nests_innermost_first(string template, string expected) => Assert.Equal(expected, R(template));

    [Theory]
    [InlineData("$parse(line%i%)$", "second")]
    [InlineData("$parse(line3)$", "global third")]
    [InlineData("$parse(~color~)$", "$parse(blue)$")]
    [InlineData("$parse(count:D2)$", "03")]
    [InlineData("$upper($parse(line%i%)$)$", "SECOND")]
    [InlineData("$parse(evil)$", "%user% $math(1+1)$ ~color~")]
    public void Parse_looks_up_built_names(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Parse_with_explicit_sigils()
    {
        _args["which"] = "~line3~";
        Assert.Equal("global third", R("$parse(%which%)$"));
        _args["which"] = "%line1%";
        Assert.Equal("first", R("$parse(%which%)$"));
    }

    [Fact]
    public void Failing_functions_stay_in_the_text_with_variables_substituted()
    {
        Assert.Equal("$math(%count% / 0)$".Replace("%count%", "3"), R("$math(%count% / 0)$"));
        Assert.Equal("x $math(abc)$ y", R("x $math(abc)$ y"));
        Assert.Equal("$parse(nothing)$", R("$parse(nothing)$"));
        Assert.Equal("$substring(Alice,x)$", R("$substring(%user%,x)$"));
        Assert.Equal("$replace(a,b)$", R("$replace(a,b)$"));
        Assert.Equal("$replace(abc,,x)$", R("$replace(abc,,x)$"));
        Assert.Equal("$math(3/0)$", R("$math(3/0)$"));
        Assert.Equal("$math(3/0)$", R("$math(3/0)$"));
        Assert.Single(_log.Recent(), e => e.Message.Contains("$math(3/0)$") && e.Message.Contains("division by zero"));
    }

    [Fact]
    public void Error_event_fires_for_every_failure()
    {
        var errors = new List<string>();
        _env.Error += errors.Add;
        R("$math(1/0)$");
        R("$math(1/0)$");
        Assert.Equal(2, errors.Count);
    }

    [Theory]
    [InlineData("\\$math(1+1)$", "$math(1+1)$")]
    [InlineData("\\$upper(%user%)$", "$upper(Alice)$")]
    [InlineData("C:\\$Recycle.Bin", "C:\\$Recycle.Bin")]
    [InlineData("C:\\Users\\%user%", "C:\\Users\\Alice")]
    [InlineData("$upper(a\\)$b)$", "A)$B")]
    [InlineData("It costs $5 (really)", "It costs $5 (really)")]
    [InlineData("$unknown(x)$", "$unknown(x)$")]
    [InlineData("$math(1+1", "$math(1+1")]
    [InlineData("$math(1+1) and more", "$math(1+1) and more")]
    [InlineData("$$math(2)$$", "$2$")]
    public void Escapes_and_non_functions(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Substituted_values_are_never_parsed_again()
    {
        Assert.Equal("%user% $math(1+1)$ ~color~", R("%evil%"));
        Assert.Equal("26", R("$length(%evil%)$"));
    }

    [Fact]
    public void Text_without_markers_is_returned_without_copying()
    {
        const string plain = "Just some text, no markers.";
        Assert.Same(plain, R(plain));
        var noVariables = string.Concat("50", "% off");
        Assert.Equal("50% off", R(noVariables));
    }

    [Theory]
    [InlineData("100% sure, %unknown% stays", "100% sure, %unknown% stays")]
    [InlineData("%a%b%user%", "%a%bAlice")]
    [InlineData("%user%%user%", "AliceAlice")]
    [InlineData("50%:off %user%", "50%:off Alice")]
    [InlineData("%user:%", "%user:%")]
    [InlineData("~missing~ ~color~", "~missing~ blue")]
    [InlineData("%user%~color~", "Aliceblue")]
    [InlineData("%gif.0.text%", "%gif.0.text%")]
    public void Keeps_existing_marker_rules(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Uses_the_environment_of_the_global_variables_instance()
    {
        using var log = new AppLog();
        var env = TestEnv.Create(log);
        using var globals = new GlobalVariables(null, log, env);
        globals.Set("x", "1", persist: false);
        var args = new Dictionary<string, object?>();
        Assert.Equal("2026 1", VariableResolver.Resolve("%date:yyyy% ~x~", args, globals.Get));
        VariableResolver.Resolve("$math(1/0)$", args, globals.Get);
        Assert.Contains(log.Recent(), e => e.Source == "Variables");
    }

    [Fact]
    public void Template_cache_handles_many_distinct_templates()
    {
        for (var i = 0; i < 5000; i++) Assert.Equal($"Alice {i}", R($"%user% {i}"));
    }
}
