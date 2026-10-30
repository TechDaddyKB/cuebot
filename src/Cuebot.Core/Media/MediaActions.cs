using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Media;

/// <summary>Registration for media requests (media-requests.json), their sub-actions and triggers.</summary>
public static class MediaFeatures
{
    public const string Category = "Media requests";

    public static IServiceCollection AddMediaFeatures(this IServiceCollection services)
    {
        services.AddSingleton<IMediaLookup>(sp => new YouTubeOEmbedLookup(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new MediaRequests(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<OverlayFeed>(), sp.GetRequiredService<IMediaLookup>(),
            sp.GetService<PointsService>(), sp.GetService<ChatHub>()));
        services.AddSingleton<ISubActionHandler, AddMediaRequestSubAction>();
        services.AddSingleton<ISubActionHandler, MediaControlSubAction>();
        services.AddSingleton<ITriggerProvider, MediaTriggers>();
        return services;
    }
}

/// <summary>Adds a YouTube video to the media request queue for a viewer (free by default).</summary>
public sealed class AddMediaRequestSubAction(MediaRequests media) : ISubActionHandler
{
    public string TypeId => "media.request";
    public string Category => MediaFeatures.Category;
    public string DisplayName => "Add media request";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("link", "YouTube link or video id") { DefaultValue = "%rawInput%" },
        new("user", "Requested by") { DefaultValue = "%user%" },
        new("platform", "Platform") { DefaultValue = "%platform%" },
        new("free", "Free (no points, no per-viewer limit)", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = p["user"].Contains('%') ? "" : p["user"].Trim();
        var platform = p["platform"].Contains('%') ? "" : p["platform"].Trim();
        var userId = context.Args.GetValueOrDefault("userId") as string is { Length: > 0 } id && user.Length > 0 ? id : user.ToLowerInvariant();
        var (item, error) = await media.RequestAsync(p["link"], platform, userId, user, subscriber: true, free: p.GetBool("free", true), ct)
            .ConfigureAwait(false);
        context.Args["success"] = item is not null;
        context.Args["error"] = error;
        context.Args["title"] = item?.Title ?? "";
        context.Args["requestId"] = item?.Id ?? "";
        return SubActionResult.Next;
    }
}

/// <summary>Skips, pauses, resumes or clears media requests, opens or closes requests, or sets the volume.</summary>
public sealed class MediaControlSubAction(MediaRequests media) : ISubActionHandler
{
    internal const string Skip = "Skip";
    internal const string Pause = "Pause";
    internal const string Resume = "Resume";
    internal const string Clear = "Clear the queue (refunds)";
    internal const string Open = "Open requests";
    internal const string Close = "Close requests";
    internal const string Volume = "Set volume";

    public string TypeId => "media.control";
    public string Category => MediaFeatures.Category;
    public string DisplayName => "Control media requests";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Do", ParameterKind.Choice) { DefaultValue = Skip, Choices = [Skip, Pause, Resume, Clear, Open, Close, Volume] },
        new("volume", "Volume (0 to 100)", ParameterKind.Number) { DefaultValue = "50" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var done = true;
        switch (p["mode"])
        {
            case Pause: media.SetPaused(true); break;
            case Resume: media.SetPaused(false); break;
            case Clear: context.Args["removed"] = media.Clear(); break;
            case Open: media.UpdateSettings(s => s.Open = true); break;
            case Close: media.UpdateSettings(s => s.Open = false); break;
            case Volume: media.UpdateSettings(s => s.Volume = p.GetInt("volume", 50)); break;
            default: done = media.Skip(); break;
        }
        context.Args["success"] = done;
        return Task.FromResult(SubActionResult.Next);
    }
}

internal sealed class MediaTriggers : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(MediaEvents.Requested, MediaFeatures.Category, "Media requested", MediaEvents.Requested),
        new EventTriggerType(MediaEvents.Started, MediaFeatures.Category, "Media request started playing", MediaEvents.Started),
        new EventTriggerType(MediaEvents.Skipped, MediaFeatures.Category, "Media request skipped", MediaEvents.Skipped),
    ];
}
