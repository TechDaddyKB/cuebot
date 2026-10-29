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
  token; a stopped turn denies. An AI app gets the token from the tool and must ask the streamer in its reply before
  calling again. Turning off "require confirmation" on the Servers page turns this off too.
- **Instructions.** Every provider gets the same system prompt (`AssistantPrompt`): who the assistant is for, look things
  up before changing them, ask before destructive changes, no passwords or keys, plain short answers.
- **Conversation.** One at a time, kept in memory; **New chat**, or changing the AI or model, starts a new one.

## Providers

The **AI** picker lists every provider; **Automatic** uses the first ready one in this order: Codex, Claude Code (AI
apps the streamer is already signed in to), then the API keys (Anthropic, OpenAI, xAI, Google).

### AI apps (CLIs)

The app's own sign-in is used; Cuebot stores nothing for it. Cuebot looks for the program on PATH and in the usual
install folders (npm, bun, mise, Homebrew, `~/.local/bin`); **Program** on the page sets the path when it lives elsewhere.
The app reaches Cuebot through the MCP server (on by default), so that server must be on. The token goes in the
environment (Codex, Claude Code) or the session request (ACP), never on a command line.

Cuebot only runs an AI app it can limit to Cuebot's tools, and each one is checked with the real app
(`AssistantCliLiveTests`: it must not be able to read a secret file in its working folder):

| App | How Cuebot runs it | Limited to Cuebot by |
| --- | --- | --- |
| **Codex** (`codex`) | `codex app-server` (the Codex IDE extensions' protocol), one process per conversation; **Sign in** starts Codex's Sign in with ChatGPT in the browser | `--disable` for every built-in tool feature (shell, unified exec, apps, plugins, browser and computer use, images, multi-agent, skills, hooks), `web_search="disabled"`, the streamer's other Codex MCP servers switched off for that process (`mcp_servers.<name>.enabled=false`), Cuebot's server added; read-only sandbox, no approvals (any approval request is declined), Cuebot's instructions as developer instructions, ephemeral (not in Codex's history). Status reads only whether `auth.json` holds a sign-in. |
| **Claude Code** (`claude`) | `claude -p` in stream-json mode, one process per conversation; Stop sends Claude Code's interrupt request | `--restricted --tools ""` (no commands, files or web), `--strict-mcp-config --mcp-config` with Cuebot's server only (`Bearer ${CUEBOT_MCP_TOKEN}` from the environment), `--allowedTools mcp__cuebot --permission-mode dontAsk`, `--system-prompt`, `--disable-slash-commands`, `--no-session-persistence`. Status: `claude auth status`. |
| Grok CLI | Not available yet | Checked with Grok CLI 1.0.67: in `grok agent stdio` it reads files, loads its own MCP servers, skills and instructions without asking, and ignores `--tools`; nothing per run turns that off. Use the xAI API key instead. |
| Gemini CLI | Not available yet | Its file tools run without asking and Cuebot has not been able to confirm a way to turn them off. Use the Google API key instead. |

The ACP driver (`AcpProvider`, Agent Client Protocol over stdio: Cuebot's MCP server in `session/new`, no file system or
terminal offered, permission requests allowed only for Cuebot's tools) is in place for Grok and Gemini once they can be
limited. The apps' own global instruction files (`~/.codex/AGENTS.md`, `~/.claude/CLAUDE.md`) may still be read by
them; their tools and MCP servers are not available. In the Flatpak build the AI apps on the host are not reachable;
use an API key there.

### API keys

| Provider | How | Default model |
| --- | --- | --- |
| Claude (Anthropic API key) | Official Anthropic C# SDK (`Anthropic` package), beta Messages API streamed: adaptive thinking, effort medium, prompt caching, `fallbacks: "default"` (`server-side-fallback-2026-07-01`) so a declined request can be answered by another model; thinking blocks go back into the history unchanged | `claude-opus-5-5` |
| OpenAI / Grok (xAI) / Gemini (Google) API keys | One OpenAI-compatible Chat Completions client (`OpenAiCompatibleProvider`), streamed, with tool calls: `api.openai.com/v1`, `api.x.ai/v1`, `generativelanguage.googleapis.com/v1beta/openai` | The newest general chat model in the provider's own `/models` list (no image, audio, embedding, mini... models), so Cuebot does not go stale |

**List models** fills a picker from the provider; the Model field accepts any id (for an AI app, empty uses the app's
own default).

## Security

- API keys are stored in `assistant.json` like other modules' keys, shown masked, never logged, never returned by the
  REST API or MCP. Provider errors are reported with the provider's message, never the key.
- The Anthropic SDK gets the key explicitly, so it reads no `ANTHROPIC_*` variable or saved profile on the computer.
- Answers stream with no client timeout (Stop cancels); each turn allows at most 30 tool rounds.
