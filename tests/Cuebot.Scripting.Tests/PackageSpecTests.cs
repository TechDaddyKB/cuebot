namespace Cuebot.Scripting.Tests;

public sealed class PackageSpecTests
{
    [Theory]
    [InlineData("requests")]
    [InlineData("requests==2.32.3")]
    [InlineData("requests[socks,security]>=2.31,<3")]
    [InlineData("python-dateutil~=2.9")]
    [InlineData("numpy!=1.26.0")]
    [InlineData("  obsws_python  ")]
    public void Accepts_requirement_specifiers(string spec) =>
        Assert.Equal(spec.Trim(), PackageSpec.ValidateRequirement(spec));

    [Theory]
    [InlineData("")]
    [InlineData("-r requirements.txt")]
    [InlineData("--index-url=https://evil.example")]
    [InlineData("requests --pre")]
    [InlineData("requests\n--upgrade")]
    [InlineData("requests\t-e")]
    [InlineData("git+https://example.com/x.git")]
    [InlineData("../local/package")]
    [InlineData("requests;rm -rf /")]
    public void Rejects_options_and_injection(string spec) =>
        Assert.Throws<ArgumentException>(() => PackageSpec.ValidateRequirement(spec));

    [Fact]
    public void Rejects_overly_long_specs() =>
        Assert.Throws<ArgumentException>(() => PackageSpec.ValidateRequirement(new string('a', 201)));

    [Theory]
    [InlineData("requests")]
    [InlineData("python-dateutil")]
    public void Accepts_package_names(string name) => Assert.Equal(name, PackageSpec.ValidateName(name));

    [Theory]
    [InlineData("-y")]
    [InlineData("requests==2.0")]
    [InlineData("a b")]
    public void Rejects_invalid_names(string name) =>
        Assert.Throws<ArgumentException>(() => PackageSpec.ValidateName(name));
}
