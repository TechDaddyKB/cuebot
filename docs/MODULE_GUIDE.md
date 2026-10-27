# Writing a Cuebot module

Cuebot is a clean-room, cross-platform (Windows + Linux) streaming automation app built on .NET 10 and Avalonia.
Modules plug platform integrations into the core action engine. Read `src/Cuebot.Core` before writing one; it is small.

## Core concepts (src/Cuebot.Core)

- **BotEvent / EventBus** (`Events/EventBus.cs`): modules publish `new BotEvent("twitch.follow", args)`.
  Event types are `module.camelCase`. Argument keys are camelCase and case-insensitive (`user`, `userName`,
  `userId`, `message`, ...). Values should be strings, numbers, or bools.
- **ITriggerType / EventTriggerType / ITriggerProvider** (`Actions/Abstractions.cs`, `Actions/Registry.cs`):
  a trigger listens to event types and filters with its settings. Most modules expose one `ITriggerProvider`
  building a table of `EventTriggerType`s.
- **ISubActionHandler**: one step of an action. Parameters are described with `ParameterDescriptor`s; the UI
  renders editors from them, so modules ship no views. Values arrive with `%arg%` / `~global~` substituted
  (except `ParameterKind.Code`). Return `SubActionResult.Next`, `.Stop`, or `.TakeBranch(name)` (declare
  `Branches`). Throwing fails the action and is logged. Sub-actions may write results into `context.Args`.
- **IScriptFunctionProvider / ScriptApi** (`Scripting/ScriptApi.cs`): functions callable from user C#/Python
  scripts, e.g. `twitch.sendMessage`. Arguments arrive as a JSON object; return a JSON-serializable value.
- **IConnectionStatus** (`ConnectionStatus.cs`): implement on each connection service for the dashboard.
- **Settings**: `services.AddSettingsFile<TSettings>("module.json")` registers a `JsonFile<TSettings>`;
  mutate `.Value` then call `.ScheduleSave()`.
- **AppLog**: `log.Info("Twitch", "...")`. Never log secrets (tokens, passwords, API keys).
- A shared `HttpClient` singleton is registered by core.

## Conventions

- Namespace `Cuebot.<Module>`; one `AddXxx(this IServiceCollection)` extension in
  `<Module>ServiceCollectionExtensions.cs` registering settings, services, handlers, triggers and script functions.
- Services start through `Task StartAsync(CancellationToken)` (auto-connect) and implement `IAsyncDisposable`.
- Background work must never throw into the void: catch, log, update status.
- Status/events are raised from background threads; the UI marshals to its own thread.
- Cross-platform only: no Windows-only APIs unless guarded with `OperatingSystem.IsWindows()` and a Linux path.
- Keep classes small and focused; prefer pure, unit-testable functions for parsing/mapping/protocol logic.
- Tests live in `tests/Cuebot.<Module>.Tests` (xunit v2). No test may touch real network services.
