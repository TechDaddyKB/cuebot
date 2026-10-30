using Cuebot.Core.Auth;

namespace Cuebot.Tests.Auth;

public sealed class AppCredentialsTests
{
    private static readonly AppCredentials BuiltIn = new("builtin-id", "builtin-secret", IsBuiltIn: true);

    [Fact]
    public void Without_an_own_app_the_built_in_one_is_used()
    {
        Assert.Same(BuiltIn, AppCredentials.Choose("", "", BuiltIn));
        Assert.Same(BuiltIn, AppCredentials.Choose("  ", "own-secret", BuiltIn));
    }

    [Fact]
    public void An_own_app_wins_even_without_a_secret()
    {
        var app = AppCredentials.Choose(" own-id ", " own-secret ", BuiltIn);
        Assert.Equal(new AppCredentials("own-id", "own-secret", false), app);

        // A missing secret is reported by the service; it never falls back to the built-in secret.
        Assert.Equal(new AppCredentials("own-id", "", false), AppCredentials.Choose("own-id", "", BuiltIn));
    }

    [Fact]
    public void Nothing_is_set_without_either_app()
    {
        var app = AppCredentials.Choose("", "", AppCredentials.None);
        Assert.False(app.IsSet);
        Assert.False(app.IsBuiltIn);
    }

    [Fact]
    public void The_secret_stays_out_of_text()
    {
        Assert.DoesNotContain("builtin-secret", BuiltIn.ToString());
        Assert.Contains("builtin-id", BuiltIn.ToString());
    }
}
