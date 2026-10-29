# Assistant

The Assistant page (Main → Assistant) is a chat for the streamer: ask an AI to build or change actions, commands and
timers, explain features, or plan an integration. It is a setup helper only; it never talks to viewers and is not
reachable from actions.

Module: `src/Cuebot.Assistant`. Settings: `assistant.json` (data folder). UI: `Cuebot.App/ViewModels/Assistant`,
`Views/Assistant`. Tests: `tests/Cuebot.Assistant.Tests`, `Cuebot.UiTests/AssistantPageTests`.

## How it works

- **Tools.** The AI works only through Cuebot's MCP tools ([SERVERS.md](SERVERS.md#mcp-server-mcp-on-the-api-listener)):
  list and describe trigger and sub-action types, create and edit actions, commands, timers and variables, and so on.
  API-key providers call them in process through `McpServer` (`AssistantTools`); AI CLIs reach the same server over
  `/mcp`. Every call is logged as `MCP: Tool call: <name>`, never with argument values. API-key providers also get
  `read_cuebot_doc` for the `cuebot://docs/actions` and `cuebot://docs/variables` guides that MCP clients read as
  resources.
- **Approval.** Destructive tools (deletes, imports over existing items, sending chat) use the MCP confirmation token:
  with an API key, Cuebot shows the summary with **Approve** / **Deny** and only an Approve repeats the call with the
  token; a stopped turn denies. Turning off "require confirmation" on the Servers page turns this off too.
- **Instructions.** Every provider gets the same system prompt (`AssistantPrompt`): who the assistant is for, look things
  up before changing them, ask before destructive changes, no passwords or keys, plain short answers.
- **Conversation.** One at a time, kept in memory; **New chat**, or changing the AI or model, starts a new one.

## Providers

The **AI** picker lists every provider; **Automatic** uses the first ready one in this order: Codex, Claude Code, Grok,
Gemini (AI CLIs the streamer is already signed in to), then the API keys (Anthropic, OpenAI, xAI, Google).

| Provider | How | Default model |
| --- | --- | --- |
| Claude (Anthropic API key) | Official Anthropic C# SDK (`Anthropic` package), beta Messages API streamed: adaptive thinking, effort medium, prompt caching, `fallbacks: "default"` (`server-side-fallback-2026-07-01`) so a declined request can be answered by another model; thinking blocks go back into the history unchanged | `claude-opus-5-5` |
| OpenAI / Grok (xAI) / Gemini (Google) API keys | One OpenAI-compatible Chat Completions client (`OpenAiCompatibleProvider`), streamed, with tool calls: `api.openai.com/v1`, `api.x.ai/v1`, `generativelanguage.googleapis.com/v1beta/openai` | The newest general chat model in the provider's own `/models` list (no image, audio, embedding, mini... models), so Cuebot does not go stale |

**List models** fills a picker from the provider; the Model field accepts any id. AI CLIs (Codex with Sign in with
ChatGPT, Claude Code, Grok, Gemini) come next and use the sign-in each app already has.

## Security

- API keys are stored in `assistant.json` like other modules' keys, shown masked, never logged, never returned by the
  REST API or MCP. Provider errors are reported with the provider's message, never the key.
- The Anthropic SDK gets the key explicitly, so it reads no `ANTHROPIC_*` variable or saved profile on the computer.
- Answers stream with no client timeout (Stop cancels); each turn allows at most 30 tool rounds.
