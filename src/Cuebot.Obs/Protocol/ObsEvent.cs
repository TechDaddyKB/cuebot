using System.Text.Json;

namespace Cuebot.Obs.Protocol;

/// <summary>An obs-websocket event (op 5), e.g. <c>CurrentProgramSceneChanged</c>.</summary>
/// <param name="EventData">The event's data object, or an undefined element when the event carries none.</param>
public sealed record ObsEvent(string EventType, JsonElement EventData);
