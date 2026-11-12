using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Profiles;
using Cuebot.Core.Storage;

namespace Cuebot.Tests.Profiles;

public sealed class ProfileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cuebot-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();

    public ProfileCatalogTests()
    {
        Directory.CreateDirectory(_root);
        SecretStore.ResetCache(_root);
    }

    public void Dispose()
    {
        SecretStore.ResetCache(_root);
        Directory.Delete(_root, recursive: true);
    }

    public sealed class Account
    {
        public string Login { get; set; } = "";
        [Secret] public string Token { get; set; } = "";
    }

    private void Write(string folder, string file, string login, string token)
    {
        using var json = new JsonFile<Account>(Path.Combine(folder, file), _log);
        json.Value.Login = login;
        json.Value.Token = token;
        json.SaveNow();
    }

    private Account Read(string folder, string file)
    {
        using var json = new JsonFile<Account>(Path.Combine(folder, file), _log);
        return json.Value;
    }

    [Fact]
    public void A_new_data_folder_gets_one_empty_profile_called_Main()
    {
        var catalog = ProfileCatalog.Open(_root);

        var main = Assert.Single(catalog.Profiles);
        Assert.Equal(("main", "Main"), (main.Id, main.Name));
        Assert.Equal(Path.Combine(_root, "profiles", "main"), catalog.ActiveFolder);
        Assert.True(Directory.Exists(catalog.ActiveFolder));
        Assert.False(catalog.MovedExistingData);
        Assert.True(File.Exists(Path.Combine(_root, ProfileCatalog.FileName)));
    }

    [Fact]
    public void Data_from_before_profiles_moves_into_Main_and_keeps_its_sign_ins()
    {
        Write(_root, "twitch.json", "kitsune", "TWITCH-TOKEN");
        Write(_root, "app.json", "drive-user", "DRIVE-TOKEN");   // shared: stays, and its secret comes along
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "overlays", "assets"));
        File.WriteAllText(Path.Combine(_root, "overlays", "assets", "alert.png"), "png");
        Directory.CreateDirectory(Path.Combine(_root, "python"));
        File.WriteAllText(Path.Combine(_root, "python", "keep.txt"), "shared");
        Assert.True(ProfileCatalog.NeedsMove(_root));
        SecretStore.ResetCache(_root);

        var catalog = ProfileCatalog.Open(_root);

        var main = catalog.ActiveFolder;
        Assert.True(catalog.MovedExistingData);
        Assert.False(ProfileCatalog.NeedsMove(_root));
        Assert.True(File.Exists(Path.Combine(main, "actions.json")));
        Assert.True(File.Exists(Path.Combine(main, "overlays", "assets", "alert.png")));
        Assert.True(File.Exists(Path.Combine(main, SecretStore.FileName)));
        Assert.False(File.Exists(Path.Combine(_root, "twitch.json")));
        Assert.True(File.Exists(Path.Combine(_root, "python", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "app.json")));
        SecretStore.ResetCache(_root);
        Assert.Equal("TWITCH-TOKEN", Read(main, "twitch.json").Token);
        Assert.Equal("DRIVE-TOKEN", Read(_root, "app.json").Token);
    }

    [Fact]
    public void An_interrupted_move_finishes_on_the_next_open()
    {
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(_root, "commands.json"), "{}");
        // As if the last open stopped half-way: one file moved, no profiles.json yet.
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "main"));
        File.Move(Path.Combine(_root, "commands.json"), Path.Combine(_root, "profiles", "main", "commands.json"));

        var catalog = ProfileCatalog.Open(_root);

        Assert.Single(catalog.Profiles);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(catalog.ActiveFolder, "actions.json")));
        Assert.True(File.Exists(Path.Combine(catalog.ActiveFolder, "commands.json")));
    }

    [Fact]
    public void A_copy_takes_the_chosen_parts_with_their_secrets_and_never_the_sign_ins()
    {
        var catalog = ProfileCatalog.Open(_root);
        var main = catalog.ActiveFolder;
        Write(main, "obs.json", "obs", "OBS-PASSWORD");
        Write(main, "twitch.json", "kitsune", "TWITCH-TOKEN");
        File.WriteAllText(Path.Combine(main, "actions.json"), "{\"actions\":[]}");
        File.WriteAllText(Path.Combine(main, "commands.json"), "{}");
        Directory.CreateDirectory(Path.Combine(main, "overlays", "widgets"));
        File.WriteAllText(Path.Combine(main, "overlays", "widgets", "w.js"), "js");

        var copy = catalog.Create("Night Owl", copyFrom: "main", parts: ProfileParts.Actions | ProfileParts.AppsAndDevices | ProfileParts.Overlays);

        var folder = catalog.FolderOf(copy.Id);
        Assert.Equal("night-owl", copy.Id);
        Assert.True(File.Exists(Path.Combine(folder, "actions.json")));
        Assert.True(File.Exists(Path.Combine(folder, "overlays", "widgets", "w.js")));
        Assert.False(File.Exists(Path.Combine(folder, "commands.json")));
        Assert.False(File.Exists(Path.Combine(folder, "twitch.json")));
        SecretStore.ResetCache(_root);
        Assert.Equal("OBS-PASSWORD", Read(folder, "obs.json").Token);
        Assert.Equal("main", catalog.Active.Id);   // making a profile does not switch to it
    }

    [Fact]
    public void Sign_in_files_are_in_no_copyable_part()
    {
        var copyable = ProfileCatalog.PartFiles.Values.SelectMany(f => f).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "twitch.json", "youtube.json", "kick.json", "rumble.json", "portal.json", "donations.json", "webhooks.json", "discord-bot.json", "relay.json" })
            Assert.DoesNotContain(file, copyable);
        Assert.DoesNotContain(copyable, ProfileCatalog.SharedEntries.Contains);
    }

    [Fact]
    public void Names_are_unique_ids_never_change_and_the_open_profile_cannot_be_deleted()
    {
        var catalog = ProfileCatalog.Open(_root);
        var a = catalog.Create("Kitsune");
        var b = catalog.Create("Kitsune!");
        Assert.Equal(("kitsune", "kitsune-2"), (a.Id, b.Id));
        Assert.Throws<ArgumentException>(() => catalog.Create("kitsune"));
        Assert.Throws<ArgumentException>(() => catalog.Create("  "));

        catalog.Rename(a.Id, "Fox");
        Assert.Equal("kitsune", catalog.Find("Fox")!.Id);
        Assert.Equal(a, catalog.Find("KITSUNE"));
        Assert.NotEqual(a.Color, b.Color);

        Assert.Throws<InvalidOperationException>(() => catalog.Delete("main"));
        catalog.Delete(b.Id);
        Assert.False(Directory.Exists(catalog.FolderOf(b.Id)));
        Assert.Null(catalog.Find(b.Id));
    }

    [Fact]
    public void The_active_profile_is_remembered_and_unknown_folders_are_listed_again()
    {
        var catalog = ProfileCatalog.Open(_root);
        var owl = catalog.Create("Night Owl");
        catalog.SetActive(owl.Id);
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "restored"));

        var again = ProfileCatalog.Open(_root);

        Assert.Equal("night-owl", again.Active.Id);
        Assert.Equal(Path.Combine(_root, "profiles", "night-owl"), ProfileCatalog.ActiveFolderOf(_root));
        Assert.NotNull(again.Find("restored"));
    }

    [Fact]
    public void Shared_folders_live_in_the_data_folder_and_everything_else_in_the_profile()
    {
        var profile = Path.Combine(_root, "profiles", "main");
        var paths = new AppPaths(profile, _root);

        Assert.Equal(Path.Combine(_root, "logs"), paths.Logs);
        Assert.Equal(Path.Combine(_root, "python"), paths.GetSharedDirectory("python"));
        Assert.Equal(Path.Combine(_root, "app.json"), paths.SharedFile("app.json"));
        Assert.Equal(Path.Combine(profile, "actions.json"), paths.File("actions.json"));
        Assert.Equal(_root, new AppPaths(_root).Shared);
    }
}
