using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Tests.Storage;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();

    public SecretStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SecretStore.ResetCache();
        Directory.Delete(_dir, recursive: true);
    }

    public sealed class Account
    {
        public string Login { get; set; } = "";
        [Secret] public string AccessToken { get; set; } = "";
    }

    public sealed class Settings
    {
        public string ClientId { get; set; } = "";
        [Secret] public string ClientSecret { get; set; } = "";
        public Account Bot { get; set; } = new();
        public List<Account> Extra { get; set; } = [];
        public Dictionary<string, Account> ByName { get; set; } = [];
    }

    private string FilePath => Path.Combine(_dir, "module.json");

    private JsonFile<Settings> Open(string? path = null) => new(path ?? FilePath, _log);

    private static Settings Sample() => new()
    {
        ClientId = "public-id",
        ClientSecret = "TOP-SECRET-1",
        Bot = new Account { Login = "bot", AccessToken = "TOP-SECRET-2" },
        Extra = [new Account { Login = "a", AccessToken = "TOP-SECRET-3" }, new Account { Login = "b" }],
        ByName = new() { ["x"] = new Account { Login = "x", AccessToken = "TOP-SECRET-4" } },
    };

    [Fact]
    public void Secrets_stay_out_of_the_settings_file_and_come_back_on_load()
    {
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
        }
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("TOP-SECRET", text);
        Assert.Contains("public-id", text);
        Assert.Contains(SecretJson.Prefix, text);
        Assert.DoesNotContain("TOP-SECRET", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, SecretStore.FileName))));

        SecretStore.ResetCache();   // as after a restart
        using var reopened = Open();
        var s = reopened.Value;
        Assert.Equal("TOP-SECRET-1", s.ClientSecret);
        Assert.Equal("TOP-SECRET-2", s.Bot.AccessToken);
        Assert.Equal("TOP-SECRET-3", s.Extra[0].AccessToken);
        Assert.Equal("", s.Extra[1].AccessToken);
        Assert.Equal("TOP-SECRET-4", s.ByName["x"].AccessToken);
    }

    [Fact]
    public void A_file_with_secrets_written_in_it_is_converted_on_load()
    {
        File.WriteAllText(FilePath, """{ "clientId": "public-id", "clientSecret": "OLD-PLAIN-SECRET", "bot": { "accessToken": "OLD-PLAIN-TOKEN" } }""");
        using (var file = Open())
        {
            Assert.Equal("OLD-PLAIN-SECRET", file.Value.ClientSecret);
            Assert.Equal("OLD-PLAIN-TOKEN", file.Value.Bot.AccessToken);
        }
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("OLD-PLAIN", text);
        SecretStore.ResetCache();
        using var reopened = Open();
        Assert.Equal("OLD-PLAIN-SECRET", reopened.Value.ClientSecret);
    }

    [Fact]
    public void A_copy_on_another_computer_has_no_secrets()
    {
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
        }
        var other = Path.Combine(_dir, "other-pc");
        Directory.CreateDirectory(other);
        File.Copy(FilePath, Path.Combine(other, "module.json"));

        using var copy = Open(Path.Combine(other, "module.json"));
        Assert.Equal("public-id", copy.Value.ClientId);
        Assert.Equal("", copy.Value.ClientSecret);
        Assert.Equal("", copy.Value.Bot.AccessToken);
    }

    [Fact]
    public void A_restored_older_file_finds_its_secrets_again_on_the_same_computer()
    {
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
        }
        var backup = File.ReadAllText(FilePath);
        using (var file = Open())
        {
            file.Value.ClientId = "changed";
            file.SaveNow();
        }
        File.WriteAllText(FilePath, backup);   // what a restore does
        SecretStore.ResetCache();
        using var restored = Open();
        Assert.Equal("public-id", restored.Value.ClientId);
        Assert.Equal("TOP-SECRET-1", restored.Value.ClientSecret);
    }

    [Fact]
    public void Clearing_a_secret_removes_it_from_the_store()
    {
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
            file.Value.ClientSecret = "";
            file.SaveNow();
        }
        SecretStore.ResetCache();
        using var reopened = Open();
        Assert.Equal("", reopened.Value.ClientSecret);
        Assert.Equal("TOP-SECRET-2", reopened.Value.Bot.AccessToken);
    }

    [Fact]
    public void An_unreadable_store_is_set_aside_and_reported()
    {
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
        }
        File.Delete(Path.Combine(_dir, FileKeyProtector.FileName));   // the key is gone
        SecretStore.ResetCache();
        var warnings = new List<string>();
        SecretStore.Warn = warnings.Add;
        try
        {
            using var reopened = Open();
            Assert.Equal("", reopened.Value.ClientSecret);
            Assert.Single(warnings);
            Assert.Contains("sign in again", warnings[0]);
            Assert.Single(Directory.GetFiles(_dir, SecretStore.FileName + ".unreadable-*"));
        }
        finally
        {
            SecretStore.Warn = null;
        }
    }

    [Fact]
    public void The_key_file_is_readable_only_by_its_owner()
    {
        if (OperatingSystem.IsWindows()) return;   // DPAPI-protected there
        using (var file = Open())
        {
            Copy(Sample(), file.Value);
            file.SaveNow();
        }
        var mode = File.GetUnixFileMode(Path.Combine(_dir, FileKeyProtector.FileName));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    private static void Copy(Settings from, Settings to)
    {
        to.ClientId = from.ClientId;
        to.ClientSecret = from.ClientSecret;
        to.Bot = from.Bot;
        to.Extra = from.Extra;
        to.ByName = from.ByName;
    }
}
