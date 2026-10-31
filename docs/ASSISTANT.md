# Assistant

The Assistant page (Main → Assistant) is a chat for the streamer: ask an AI to build or change actions, commands and
timers, explain features, or plan an integration. It is a setup helper only; it never talks to viewers and is not
reachable from actions.

Module: `src/Cuebot.Assistant`. Settings: `assistant.json` (data folder). UI: `Cuebot.App/ViewModels/Assistant`,
`Views/Assistant`. Tests: `tests/Cuebot.Assistant.Tests`, `Cuebot.UiTests/AssistantPageTests`.

## How it works

- **Tools.** The AI works only through Cuebot's MCP tools ([SERVERS.md](SERVERS.md#mcp-server-mcp-on-the-api-listener)):
  build and test actions (triggers and sub-actions), commands, timers and variables; run any sub-action once; start polls,
  predictions and giveaways; build overlays, add widgets and edit their settings and the alert box's designs; manage
  quotes, points, groups, the store, media requests, speech voices, hotkeys, decks and connections; and send sample
  events to try alerts. Sign-ins, API keys, server settings and tokens stay out of reach (cuebot://docs/capabilities).
  API-key providers call them in process through `McpServer` (`AssistantTools`); AI CLIs reach the same server over
  `/mcp`. Every call is logged as `MCP: Tool call: <name>`, never with argument values. API-key providers also get
  `read_cuebot_doc` for the `cuebot://docs/actions` and `cuebot://docs/variables` guides that MCP clients read as
  resources.
- **Approval.** Destructive tools (deletes, imports over existing items, sending chat) wait for the streamer: Cuebot
  shows the summary with **Approve** / **Deny** in the conversation, and the call goes ahead only on Approve; Deny, Stop
  or the AI giving up on the call denies. Every model is told to call such a tool straight away rather than ask in its
  reply first, so the streamer is asked once. With an API key the tools run in process; an AI app's conversation gets
  its own MCP token (`McpServer.RegisterAssistant`: valid on `/mcp` only, never the REST API, gone when the conversation
  ends), and Cuebot's MCP server asks the page directly while the AI app's call waits (tool timeout raised to 15
  minutes for Codex and Claude Code; Grok's default is longer). Other MCP clients still get the confirmation token to
  relay. Turning off "require confirmation" on the Servers page turns this off too.
- **Instructions.** Every provider gets the same system prompt (`AssistantPrompt`): who the assistant is for, look things
  up before changing them, ask before destructive changes, no passwords or keys, plain short answers.
- **Conversation.** One at a time; **New chat**, or changing the AI or model, starts a new one. The page keeps the last
  conversation in `assistant-history.json` (newest 200 lines) and shows it again after a restart, marked as one the AI
  does not remember. Answers render as Markdown (headings, lists, code, quotes, tables as monospace, bold, italic,
  inline code; links open only for http and https).

## Providers

The **AI** picker lists every provider; **Automatic** uses the first ready one in this order: Codex, Claude Code, Grok,
Antigravity (AI apps the streamer is already signed in to), then the API keys (Anthropic, OpenAI, xAI, Google).

### AI apps (CLIs)

The app's own sign-in is used; Cuebot stores nothing for it. Cuebot looks for the program on PATH and in the usual
install folders (npm, bun, mise, Homebrew, `~/.local/bin`); **Program** on the page sets the path when it lives elsewhere.
The app reaches Cuebot through the MCP server (on by default), so that server must be on. The token goes in the
environment (Codex, Claude Code) or the session request (ACP), never on a command line.

Cuebot only runs an AI app it can limit to Cuebot's tools, and each one is checked with the real app
(`AssistantCliLiveTests`: it must not be able to read a secret file in its working folder):

| App | How Cuebot runs it | Limited to Cuebot by |
| --- | --- | --- |
| **Codex** (`codex`) | `codex app-server` (the Codex IDE extensions' protocol), one process per conversation; **Sign in** starts Codex's Sign in with ChatGPT in the browser | `--disable` for every built-in tool feature (shell, unified exec, apps, plugins, browser and computer use, images, multi-agent, skills, hooks), `web_search="disabled"`, the streamer's other Codex MCP servers switched off for that process (`mcp_servers.<name>.enabled=false`), Cuebot's server added; read-only sandbox, approvals on request: Codex asks before MCP tools marked destructive and only Cuebot's server's are accepted (Cuebot's card then decides); any command or file-change request is declined, Cuebot's instructions as developer instructions, ephemeral (not in Codex's history). Status reads only whether `auth.json` holds a sign-in. |
| **Claude Code** (`claude`) | `claude -p` in stream-json mode, one process per conversation; Stop sends Claude Code's interrupt request | `--restricted --tools ""` (no commands, files or web), `--strict-mcp-config --mcp-config` with Cuebot's server only (`Bearer ${CUEBOT_MCP_TOKEN}` from the environment), `--allowedTools mcp__cuebot --permission-mode dontAsk`, `--system-prompt`, `--disable-slash-commands`, `--no-session-persistence`. Status: `claude auth status`. |
| **Grok CLI** (`grok`) | `grok agent stdio` (Agent Client Protocol), one process per conversation, with the streamer's existing Grok sign-in | `GrokIsolation`: a throwaway `GROK_HOME` per conversation in the temp folder (owner-only; none of the streamer's Grok MCP servers, skills or settings), every Claude/Cursor/Codex import off (`GROK_*_ENABLED=false`; `0` is not honoured), an agent profile in `session/new` whose tools are only `search_tool` and `use_tool` (so only the MCP servers connected, Cuebot's) and which carries Cuebot's instructions. Sign-in: Grok's documented external auth provider, a small command that prints the current access token read from `~/.grok/auth.json`; Cuebot never writes that file or uses its refresh token, and deletes the throwaway home (with Grok's copy of the access token) when the conversation ends. When the token is about to expire, opening Grok once refreshes it. Without these steps Grok 1.0.67's agent mode reads files and uses the streamer's MCP servers and instruction files without asking, and ignores `--tools`, project `.grok/config.toml` rules (untrusted folder) and `GROK_CONFIG` deny rules. |
| **Antigravity CLI** (`agy`) | `agy -p` in stream-json mode (one process per conversation, `{"event":"user","message":{...}}` lines in, `step_update`/`result` events out), with the streamer's own Antigravity sign-in. On Windows the hook command is written for PowerShell (Antigravity's shell there); that path is not yet verified with agy on Windows. | `AntigravityIsolation`: a throwaway workspace per conversation in the temp folder (owner-only) holding `AGENTS.md` (Cuebot's instructions, read as project rules) and a `cuebot` plugin (`.agents/plugins/cuebot`: Cuebot's MCP server with its bearer token in an owner-only file, and a `PreToolUse` hook on every tool). The hook runs `Cuebot --assistant-tool-gate`, which allows only `call_mcp_tool` on Cuebot's server (`cuebot_cuebot`) and reads of the tool descriptions Antigravity keeps for it (`~/.gemini/antigravity-cli/mcp/cuebot_cuebot`, symlinks and `..` refused); every other tool (shell, files, browser, web, images, subagents, scheduling...) is hard-denied, also tools a later Antigravity adds. Cuebot's server is granted with session-only `--settings` (`permissions.allow: ["mcp(cuebot_cuebot/*)"]`, never saved) because print mode can not ask. Without this, agy 1.4.10's print mode reads files and runs commands without asking. Conversations still appear in Antigravity's own history. |

The ACP driver (`AcpProvider`, Agent Client Protocol over stdio: Cuebot's MCP server in `session/new`, no file system or
terminal offered, permission requests allowed only for Cuebot's tools) runs Grok. Codex, Claude Code and Antigravity may still read their own global instruction files (`~/.codex/AGENTS.md`,
`~/.claude/CLAUDE.md`, `~/.gemini/GEMINI.md`); Grok reads none. None of them gets its tools or MCP servers. In the Flatpak build the AI apps on the host are not reachable;
use an API key there.

### API keys

| Provider | How | Default model |
| --- | --- | --- |
| Claude (Anthropic API key) | Official Anthropic C# SDK (`Anthropic` package), beta Messages API streamed: adaptive thinking, effort medium, prompt caching, `fallbacks: "default"` (`server-side-fallback-2026-07-01`) so a declined request can be answered by another model; thinking blocks go back into the history unchanged | `claude-opus-5-5` |
| OpenAI API key | OpenAI's Responses API (`/v1/responses`), streamed, `store: false` with the model's output items (encrypted reasoning included) sent back unchanged each round; OpenAI's newer reasoning models only call tools there |
| Grok (xAI) / Gemini (Google) API keys | One OpenAI-compatible Chat Completions client (`OpenAiCompatibleProvider`), streamed, with tool calls: `api.x.ai/v1`, `generativelanguage.googleapis.com/v1beta/openai` | The newest general chat model in the provider's own `/models` list (no image, audio, embedding, mini... models), so Cuebot does not go stale |

### Local model

**Local model (Ollama, LM Studio)** (`LocalModelProvider`, id `local`) runs a model on the streamer's computer (or
network). Settings: **Server** (`ServerUrl`, empty = Ollama on `http://127.0.0.1:11434`; a trailing `/v1` is fine),
**Memory** (`ContextLength`, default 32768), **Let it think first** (`Thinking`, default off) and **Give it every tool**
(`AllTools`, default off). Automatic lists it last, so it is used only when nothing else is ready.

- **Ollama** (detected by `GET /api/version`) is driven through its own `/api/chat`, streamed: that is the only way to
  set the context size (`options.num_ctx`) and switch thinking off (`think: false`). Ollama's default context depends
  on VRAM (4K on small cards), which would silently cut off Cuebot's tools. Status and **List models** use
  `/api/tags`; embedding models are left out. An empty Model picks `qwen3.5:9b`, `qwen3.5:4b`, other Qwen3.5/Qwen3,
  then Gemma 4, else the first listed.
- **Other servers** (LM Studio, llama.cpp's `llama-server`) use the OpenAI-style `/v1/chat/completions` and
  `/v1/models`, with `reasoning_effort: "none"` unless thinking is on. Their context size is set in the server.
- **Focused tools** (`LocalToolSet`): list/get/create/update/delete for actions, commands and timers, `test_action`, the
  trigger and sub-action catalogue and `read_cuebot_doc` (19 tools, a few thousand tokens, instead of 117 and about 14K),
  plus `find_cuebot_tools`, which matches its search words against the other tools and switches the best eight on for
  the rest of the conversation. The deletes are in the focused set because without them a small model asked to delete
  something switched it off instead.
- **Shorter instructions** (`AssistantPrompt.BuildLocal`): the full prompt made Qwen3.5 9B (thinking off) answer with
  nothing for some requests every time; dropping any one of its paragraphs fixed it, so local models get a short prompt
  with the one rule they most often missed (a chat command needs a command and a `command.triggered` action).
- **Small-model fixes:** a 5xx before any output is retried once (Qwen3.5 sometimes writes a tool call Ollama cannot
  parse); an empty answer gets one "please answer" or "continue" nudge per turn; tool results are cut to 12,000
  characters; when Ollama reports the prompt filled the context, the answer says so.

Measured on an RTX 4090 with Ollama 0.40.13 (31 October 2026), through the real provider on a complete Cuebot
(`LocalModelBuildTests`, five runs each; it checks what was built, not the answer):

| Request | Qwen3.5 9B | Qwen3.5 4B |
| --- | --- | --- |
| !lurk command that thanks the viewer by name | 5/5 | 3/5 |
| Moderator-only !pausealerts / !resumealerts for alerts and TTS (2 commands, 2 actions, 2 steps each) | 4/5 | 1/5 |
| Socials message every 15 minutes (timer plus action) | 5/5 | 4/5 |

Before the focused tools and short prompt (same models, OpenAI-style API, all 117 tools) they managed 1 to 2 of 4 such
requests. VRAM at 32K context: about 6.2 GB (9B), 3.8 GB (4B). CPU only (Ryzen 9 7950X3D, 8 threads): one request takes
about 26 s (4B) or 48 s (9B); a whole build ran from 30 s (thinking off) to over 5 minutes (thinking on). Known small-model
pitfalls, also on the website's local model page: they say "done" when an action is missing or not linked, sometimes ask
the streamer to type "yes" instead of calling the delete tool (Cuebot still shows Approve), and occasionally do
something other than what was asked.

Live checks: `CUEBOT_LIVE_LOCAL_MODEL=1` runs `AssistantCliLiveTests.Local_model_*` (list and delete through Cuebot's
MCP tools) and `Cuebot.UiTests` `LocalModelBuildTests` (the table above); `CUEBOT_LIVE_LOCAL_SERVER` points at another
server and `CUEBOT_LIVE_LOCAL_MODEL_NAME` picks the model.

### Models

The Model field accepts any id (empty: the provider's default). The list next to **List models** fills by itself when
the AI is ready, and again whenever another provider is picked:

| Provider | Where the list comes from |
| --- | --- |
| Anthropic, OpenAI, xAI, Google | The provider's models API (needs the key) |
| Codex | `model/list` on Codex's app-server (hidden models left out) |
| Claude Code | Its `--model` aliases: fable, opus, sonnet, haiku (it has no list command) |
| Grok CLI | `grok models`, run in a throwaway `GROK_HOME` like the conversations (sign-in passed in read-only); Grok 1.0.67 lists only its standard models there, so a model only some accounts have must be typed |
| Antigravity CLI | `agy models` |
| Local model | Ollama's `/api/tags` or the server's `/v1/models` |

## Security

- API keys are stored in `assistant.json` like other modules' keys, shown masked, never logged, never returned by the
  REST API or MCP. Provider errors are reported with the provider's message, never the key.
- The Anthropic SDK gets the key explicitly, so it reads no `ANTHROPIC_*` variable or saved profile on the computer.
- Answers stream with no client timeout (Stop cancels); each turn allows at most 30 tool rounds.
