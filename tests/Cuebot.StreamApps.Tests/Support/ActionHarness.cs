using Cuebot.Core;
using Cuebot.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tests.Support;

/// <summary>Runs sub-actions through a real <see cref="ActionEngine"/> and returns the arguments they left behind.</summary>
internal static class ActionHarness
{
    public static async Task<(bool Ok, Dictionary<string, object?> Args, string? Error)> RunAsync(
        IEnumerable<ISubActionHandler> handlers, params (string Type, Dictionary<string, string> Parameters)[] steps)
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-streamapps-run-").FullName;
        var capture = new CaptureStep();
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(dir));
        foreach (var handler in handlers) services.AddSingleton(handler);
        services.AddSingleton<ISubActionHandler>(capture);
        await using (var provider = services.BuildServiceProvider())
        {
            var engine = provider.GetRequiredService<ActionEngine>();
            var action = new ActionDefinition
            {
                Name = "harness",
                SubActions = [.. steps.Select(s => new SubActionDefinition { Type = s.Type, Parameters = s.Parameters }), new SubActionDefinition { Type = "test.capture" }],
            };
            var ok = await engine.RunAsync(action);
            var error = engine.History.Entries.Select(e => e.Error).LastOrDefault(e => e is not null);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
            return (ok && capture.Args is not null, capture.Args ?? [], error);
        }
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
