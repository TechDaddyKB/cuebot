# Command-line options

Most people never need this page. Command-line options are extra words you add when you start Cuebot, to change how it
starts. For example, on Windows you can add them to a shortcut: right-click the shortcut, choose **Properties**, and
add them at the end of **Target**.

| Option | What it does |
| --- | --- |
| `--minimized` | Start hidden in the tray (or minimized when there is no tray). Same as the **Start minimized** box on the **Settings** page. |
| `--headless` | Start with no window at all. Everything else runs, and the log is written to the terminal and the log file. Good for a computer without a screen. Stop it with ++ctrl+c++. |
| `--data <folder>` | Use another [data folder](backups-and-your-data.md). For a second setup or a USB stick. |
| `--reset-window` | Forget where the window was and how big it was. Use it when the window is off-screen. |
| `--shutdown` | Close a Cuebot that is already running. |
| `--mcp-stdio` | For AI apps: lets an app like Claude Desktop talk to a running Cuebot. See [AI tools](assistant.md#using-your-own-ai-app-with-cuebot-mcp). |
| `--backend=wayland` / `--backend=x11` | Linux only: choose how the window is drawn. See [Linux](linux.md#wayland-and-x11). |
| `--help` | List the options. |

Every option also works with `/` instead of `--`, like Streamer.bot's `/headless` and `/resetwindow`.

## Environment variables

These are settings you put in your computer's environment instead of on the command line.

| Variable | What it does |
| --- | --- |
| `CUEBOT_DATA` | The data folder, like `--data`. `--data` wins if you use both. |
| `CUEBOT_BACKEND` | Linux: `wayland` or `x11`. |
| `CUEBOT_WAYLAND_TIMEOUT` | Linux: seconds to wait for a Wayland window before trying X11 (20 if not set). |

## Only one Cuebot at a time

If you start Cuebot while it is already running, the running window comes to the front and the new one closes. That
way two copies can never write the same files. Copies with **different** data folders can run side by side.
