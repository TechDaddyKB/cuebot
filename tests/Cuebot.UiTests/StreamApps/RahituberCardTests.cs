using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Cuebot.App.ViewModels.StreamApps;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Rahituber;

namespace Cuebot.UiTests.StreamApps;

/// <summary>
/// The RahiTuber card lists the avatar's states (read from RahiTuber's folder) with their types, starts with an empty
/// test box, and its test buttons never press a Permanent state.
/// </summary>
public sealed class RahituberCardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-rahituber-").FullName;

    private sealed class FakeRahituber : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?')));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"state\":0, \"active\":1}\n", Encoding.UTF8, "application/json"),
            });
        }
    }

    [AvaloniaFact]
    public async Task Lists_states_with_types_and_never_test_presses_a_permanent_state()
    {
        var folder = Path.Combine(_dir, "RahiTuber");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "lastLayers.xml"), """
            <layers><hotkeys>
              <hotkey name="Hat" activeType="1"/>
              <hotkey name="Forever" activeType="2"/>
            </hotkeys></layers>
            """);
        var log = new AppLog();
        using var settings = new JsonFile<StreamAppsSettings>(Path.Combine(_dir, "streamapps.json"), log);
        var fake = new FakeRahituber();
        var rahi = new RahituberService(settings, log, new HttpClient(fake));
        var card = new RahituberCardViewModel(rahi);

        Assert.Equal("", card.TestState);
        Assert.Empty(card.States);
        Assert.StartsWith("Set RahiTuber's folder", card.StatesInfo);

        card.Folder = folder;
        Assert.Equal(folder, settings.Value.Rahituber.Folder);
        Assert.Equal(["0: Hat - Held", "1: Forever - Permanent (can't be undone)"], card.States.Select(s => s.Name));
        Assert.Equal("States of lastLayers:", card.StatesInfo);

        await card.States[1].RunCommand.ExecuteAsync(null);
        Assert.Contains("Permanent state", card.Info);
        card.TestState = "Forever";
        await card.SendTestCommand.ExecuteAsync(null);
        Assert.Empty(fake.Queries);

        await card.States[0].RunCommand.ExecuteAsync(null);
        Assert.Equal(["[\"Hat\",1]"], fake.Queries);
        Assert.Equal("RahiTuber got 'Hat' (Held). Watch your avatar.", card.Info);

        await card.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("RahiTuber answers at 127.0.0.1:8000; avatar lastLayers: 2 states", card.Info);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
