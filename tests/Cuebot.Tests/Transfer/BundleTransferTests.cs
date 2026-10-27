using System.Text;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Transfer;

namespace Cuebot.Tests.Transfer;

public sealed class BundleTransferTests : IDisposable
{
    private readonly TransferTestEnv _source = new();
    private readonly TransferTestEnv _target = new();

    private readonly Guid _helperId = Guid.NewGuid();
    private readonly Guid _mainId = Guid.NewGuid();
    private readonly Guid _commandId = Guid.NewGuid();
    private readonly Guid _timerId = Guid.NewGuid();
    private readonly Guid _queueId = Guid.NewGuid();

    public BundleTransferTests()
    {
        _source.Edit(p =>
        {
            p.Queues.Add(new QueueDefinition { Id = _queueId, Name = "Alerts", Blocking = true });
            p.Timers.Add(new TimerDefinition { Id = _timerId, Name = "Promo", IntervalSeconds = 600, MinChatLines = 3 });
            p.Actions.Add(new ActionDefinition
            {
                Id = _mainId, Name = "Main", Group = "Demo", Queue = "Alerts",
                Triggers =
                [
                    new TriggerDefinition { Type = "command.triggered", Settings = { ["command"] = _commandId.ToString() } },
                    new TriggerDefinition { Type = "core.timer", Settings = { ["timer"] = "Promo" } },
                ],
                SubActions =
                [
                    new SubActionDefinition { Type = "chat.send", Parameters = { ["message"] = "Hello %user%" } },
                    new SubActionDefinition
                    {
                        Type = "core.if", Parameters = { ["left"] = "%user%", ["operator"] = "equals", ["right"] = "bob" },
                        Branches = { ["True"] = [new SubActionDefinition { Type = "core.runAction", Parameters = { ["action"] = "Helper" } }], ["False"] = [] },
                    },
                    new SubActionDefinition { Type = "core.runAction", Parameters = { ["action"] = "%dynamic%" } },
                ],
                Layout = new NodeLayout(10, 20),
            });
            p.Actions.Add(new ActionDefinition
            {
                Id = _helperId, Name = "Helper",
                SubActions = [new SubActionDefinition { Type = "code.csharp", Parameters = { ["code"] = "return true;" } }],
            });
            p.Actions.Add(new ActionDefinition { Name = "Unrelated" });
        });
        _source.Commands.Save(new CommandDefinition { Id = _commandId, Name = "Hello", Commands = ["!hello", "!hi"], GlobalCooldownSeconds = 5 });
        _source.Commands.Save(new CommandDefinition { Name = "Other", Commands = ["!other"] });
        _source.Globals.SetValue("count", 5L, persist: true);
        _source.Globals.SetValue("label", "x", persist: false);
    }

    private ExportSelection SelectMain() => new()
    {
        ActionIds = { _mainId }, GlobalNames = { "count", "label" },
        Header = new TransferHeader { Name = "Demo pack", Author = "Kris", Description = "Test", BundleVersion = "1.0" },
    };

    [Fact]
    public void Export_includes_dependencies_of_the_selected_actions()
    {
        var bundle = _source.Service.Export(SelectMain());
        Assert.Equal(["Main", "Helper"], bundle.Actions.Select(a => a.Name));
        Assert.Equal("Alerts", Assert.Single(bundle.Queues).Name);
        Assert.Equal("Hello", Assert.Single(bundle.Commands).Name);
        Assert.Equal("Promo", Assert.Single(bundle.Timers).Name);
        Assert.Equal(2, bundle.Globals.Count);
        Assert.Equal("Kris", bundle.Header.Author);
        Assert.StartsWith("Cuebot", bundle.Header.Generator);

        var bare = _source.Service.Export(new ExportSelection { ActionIds = { _mainId }, IncludeDependencies = false });
        Assert.Single(bare.Actions);
        Assert.Empty(bare.Queues);
        Assert.Empty(bare.Commands);
    }

    [Fact]
    public async Task Round_trip_into_a_fresh_profile_equals_the_original()
    {
        var text = _source.Service.ExportString(SelectMain());
        Assert.StartsWith(TransferCodec.StringPrefix, text);

        var plan = _target.Service.Preview(text);
        Assert.Equal(PayloadKind.Cuebot, plan.Source);
        Assert.All(plan.Items, i => Assert.False(i.HasConflict));
        Assert.Contains(plan.Items, i => i.Name == "Helper" && i.ContainsCode);
        var changes = await _target.Service.ApplyAsync(plan);
        Assert.Equal(7, changes.Added);

        var src = _source.Profile.Snapshot;
        var dst = _target.Profile.Snapshot;
        foreach (var name in new[] { "Main", "Helper" })
            Assert.Equal(TransferTestEnv.Canon(src.FindAction(name)), TransferTestEnv.Canon(dst.FindAction(name)));
        Assert.Equal(TransferTestEnv.Canon(src.Queues), TransferTestEnv.Canon(dst.Queues));
        Assert.Equal(TransferTestEnv.Canon(src.Timers), TransferTestEnv.Canon(dst.Timers));
        Assert.Equal(TransferTestEnv.Canon(_source.Commands.Get(_commandId)), TransferTestEnv.Canon(_target.Commands.Get(_commandId)));
        Assert.Equal(5L, _target.Globals.GetValue("count"));
        Assert.False(_target.Globals.IsPersisted("label"));
        Assert.Null(dst.FindAction("Unrelated"));
    }

    [Fact]
    public void File_form_is_plain_json_and_reads_back()
    {
        var bundle = _source.Service.Export(SelectMain());
        var json = TransferCodec.ToJson(bundle);
        Assert.Contains("\"format\": \"cuebot.bundle\"", json);
        var back = TransferCodec.DecodeBundle(json);
        Assert.Equal(TransferTestEnv.Canon(bundle), TransferTestEnv.Canon(back));
        // Line-wrapped strings (pasted from chat or email) still decode.
        var wrapped = string.Join("\n", TransferCodec.Encode(bundle).Chunk(60).Select(c => new string(c)));
        Assert.Equal(TransferTestEnv.Canon(bundle), TransferTestEnv.Canon(TransferCodec.DecodeBundle(wrapped)));
    }

    [Fact]
    public async Task Add_as_new_renames_and_remaps_references()
    {
        // The target already has a different "Helper" and a "Hello" command with another id.
        var existingHelper = Guid.NewGuid();
        _target.Edit(p => p.Actions.Add(new ActionDefinition { Id = existingHelper, Name = "Helper" }));
        _target.Commands.Save(new CommandDefinition { Name = "Hello", Commands = ["!hello"] });

        var plan = _target.Service.Preview(_source.Service.ExportString(SelectMain()));
        var helper = plan.Items.Single(i => i.Kind == TransferKind.Action && i.Name == "Helper");
        var command = plan.Items.Single(i => i.Kind == TransferKind.Command);
        Assert.Equal(ConflictKind.SameName, helper.Conflict);
        Assert.Equal(existingHelper, helper.ConflictTargetId);
        Assert.Equal(ConflictKind.SameName, command.Conflict);
        helper.Mode = ImportMode.AddAsNew;
        command.Mode = ImportMode.AddAsNew;

        var changes = await _target.Service.ApplyAsync(plan);
        Assert.Contains("Helper → Helper (2)", changes.Renamed);
        var snapshot = _target.Profile.Snapshot;
        Assert.Equal(2, snapshot.Actions.Count(a => a.Name.StartsWith("Helper", StringComparison.Ordinal)));
        var copy = snapshot.FindAction("Helper (2)")!;
        Assert.NotEqual(_helperId, copy.Id);
        Assert.NotEqual(_source.Profile.Snapshot.FindAction("Helper")!.SubActions[0].Id, copy.SubActions[0].Id);

        var main = snapshot.FindAction("Main")!;
        Assert.Equal("Helper (2)", main.SubActions[1].Branches["True"][0].Parameters["action"]);
        Assert.Equal("%dynamic%", main.SubActions[2].Parameters["action"]); // runtime references are left alone
        var newCommand = _target.Commands.Snapshot.Single(c => c.Name == "Hello (2)");
        Assert.NotEqual(_commandId, newCommand.Id);
        Assert.Equal(newCommand.Id.ToString(), main.Triggers[0].Settings["command"]);
    }

    [Fact]
    public async Task Overwrite_replaces_in_place_and_keeps_the_existing_id()
    {
        var existing = Guid.NewGuid();
        _target.Edit(p =>
        {
            p.Actions.Add(new ActionDefinition { Name = "Before" });
            p.Actions.Add(new ActionDefinition { Id = existing, Name = "helper", Group = "old" });
        });
        var plan = _target.Service.Preview(_source.Service.ExportString(SelectMain()));
        var helper = plan.Items.Single(i => i.Kind == TransferKind.Action && i.Name == "Helper");
        Assert.Equal(ImportMode.Overwrite, helper.Mode);

        var changes = await _target.Service.ApplyAsync(plan);
        Assert.Equal(1, changes.Overwritten);
        var snapshot = _target.Profile.Snapshot;
        Assert.Equal(existing, snapshot.Actions[1].Id);
        Assert.Equal("Helper", snapshot.Actions[1].Name);
        Assert.Equal("code.csharp", Assert.Single(snapshot.Actions[1].SubActions).Type);
        Assert.Equal(3, snapshot.Actions.Count);

        // Importing the same string again: everything conflicts by id and overwrites, nothing is duplicated.
        var again = _target.Service.Preview(_source.Service.ExportString(SelectMain()));
        Assert.Contains(again.Items, i => i.Kind == TransferKind.Action && i.Name == "Main" && i.Conflict == ConflictKind.SameId);
        await _target.Service.ApplyAsync(again);
        Assert.Equal(3, _target.Profile.Snapshot.Actions.Count);
        Assert.Single(_target.Commands.Snapshot);
    }

    [Fact]
    public async Task Skip_and_excluded_items_are_not_imported_and_references_fall_back()
    {
        _target.Edit(p => p.Actions.Add(new ActionDefinition { Name = "HELPER" }));
        var plan = _target.Service.Preview(_source.Service.ExportString(SelectMain()));
        plan.Items.Single(i => i.Name == "Helper").Mode = ImportMode.Skip;
        plan.Items.Single(i => i.Kind == TransferKind.Timer).Include = false;
        plan.Items.Single(i => i.Name == "label").Include = false;

        var changes = await _target.Service.ApplyAsync(plan);
        Assert.Equal(3, changes.Skipped);
        var snapshot = _target.Profile.Snapshot;
        Assert.Equal(2, snapshot.Actions.Count);
        Assert.Empty(snapshot.Timers);
        Assert.Equal("HELPER", snapshot.FindAction("Main")!.SubActions[1].Branches["True"][0].Parameters["action"]);
        Assert.Null(_target.Globals.GetValue("label"));
    }

    [Fact]
    public void Preview_warns_about_references_that_do_not_resolve()
    {
        var plan = _target.Service.Preview(_source.Service.ExportString(new ExportSelection { ActionIds = { _mainId }, IncludeDependencies = false }));
        var main = Assert.Single(plan.Items);
        Assert.Contains(main.Warnings, w => w.Contains("'Helper'"));
        Assert.Contains(main.Warnings, w => w.Contains("'Alerts'"));
        Assert.Contains(main.Warnings, w => w.Contains("'Promo'"));
        Assert.DoesNotContain(main.Warnings, w => w.Contains("%dynamic%"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world, not an import")]
    [InlineData("CUEBOT1:not*base64!")]
    [InlineData("CUEBOT1:aGVsbG8=")] // valid base64, not gzip
    [InlineData("{ \"format\": \"cuebot.bundle\", \"actions\": 5 }")]
    [InlineData("{ broken json")]
    [InlineData("[1,2,3]")]
    public void Corrupted_input_is_rejected_safely(string text) =>
        Assert.Throws<TransferFormatException>(() => _target.Service.Preview(text));

    [Fact]
    public void Truncated_string_is_rejected()
    {
        var text = _source.Service.ExportString(SelectMain());
        Assert.Throws<TransferFormatException>(() => _target.Service.Preview(text[..(text.Length / 2)]));
    }

    [Fact]
    public void Newer_format_versions_are_refused()
    {
        var ex = Assert.Throws<TransferFormatException>(() =>
            TransferCodec.DecodeBundle("{\"format\":\"cuebot.bundle\",\"version\":99}"));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void Zip_bombs_are_rejected_by_the_decompressed_size_cap()
    {
        var bomb = TransferTestEnv.Gzip(new byte[TransferCodec.MaxDecompressedBytes + 1024]);
        Assert.True(bomb.Length < 100_000);
        var cuebot = Assert.Throws<TransferFormatException>(() => TransferCodec.Decode(TransferCodec.StringPrefix + Convert.ToBase64String(bomb)));
        Assert.Contains("MB", cuebot.Message);
        var sb = Assert.Throws<TransferFormatException>(() =>
            TransferCodec.Decode(Convert.ToBase64String([.. "SBAE"u8.ToArray(), .. bomb])));
        Assert.Contains("MB", sb.Message);
    }

    [Fact]
    public void Oversized_input_is_rejected_before_decoding()
    {
        var huge = new StringBuilder(TransferCodec.MaxInputChars + 2).Append('A', TransferCodec.MaxInputChars + 1).ToString();
        Assert.Throws<TransferFormatException>(() => TransferCodec.Decode(huge));
    }

    [Fact]
    public void Unique_names_count_up()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "a (2)" };
        Assert.Equal("A (3)", BundleImporter.UniqueName("A", taken));
        Assert.Equal("B", BundleImporter.UniqueName("B", taken));
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }
}
