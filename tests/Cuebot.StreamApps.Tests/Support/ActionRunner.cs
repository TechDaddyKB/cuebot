using Cuebot.Core;
using Cuebot.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tests.Support;

/// <summary>Runs sub-actions through a real <see cref="ActionEngine"/> and captures the arguments afterwards.</summary>
internal sealed class ActionRunner : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-streamapps-run-").FullName;
    private readonly ServiceProvider _services;
    private readonly CaptureStep _capture = new();

    public ActionRunner(IEnumerable<ISubActionHandler> handlers)
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir));
        foreach (var h in handlers) services.AddSingleton(h);
        services.AddSingleton<ISubActionHandler>(_capture);
        _services = services.BuildServiceProvider();
    }

    public ActionEngine Engine => _services.GetRequiredService<ActionEngine>();

    /// <summary>Runs the steps then a capture; returns (succeeded, args after the last step).</summary>
    public async Task<(bool Ok, Dictionary<string, object?> Args)> RunAsync(params (string Type, Dictionary<string, string>? Parameters)[] steps)
    {
        var action = new ActionDefinition
        {
            Name = "test " + Guid.NewGuid().ToString("N"),
            SubActions = [.. steps.Select(s => new SubActionDefinition { Type = s.Type, Parameters = s.Parameters ?? [] }), new SubActionDefinition { Type = "test.capture" }],
        };
        var ok = await Engine.RunAsync(action);
        return (ok, _capture.Args ?? []);
    }

    public string? LastError => Engine.History.Entries.LastOrDefault(e => e.Error is not null)?.Error;

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private sealed class CaptureStep : ISubActionHandler
    {
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => "test.capture";
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
