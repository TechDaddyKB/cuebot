# Writing code (C# and Python)

Most streamers never need code. The normal [sub-actions](../reference/sub-actions/index.md) do almost everything.
When they cannot do what you want, a **code step** lets you write a small program instead.

Cuebot understands two programming languages:

- **C#** (say "C sharp"). Code written for Streamer.bot works here without changes.
- **Python**. Cuebot installs Python for you the first time you need it.

!!! warning "Only run code you trust"
    Code runs with full access to your computer, like any program you start. It can read your files and use the
    internet. Only paste code from people you trust, and read it first.

## Where the code goes

1. Open an action (see [How actions work](actions-and-triggers.md)).
2. On the **Sub-actions** tab click **+ Sub-action**, type `code`, and pick **Run C# code** or **Run Python code**.
3. Type or paste your code into the **Code** box.
4. Click **Run** to try it now, or **Test trigger…** to try it with pretend variables like `user=Bob`.

Problems show up in the action's history and in the log. For C#, the message names the line and column, like
`line 6, column 5`.

Every setting of the code steps is explained on the [Code sub-actions](../reference/sub-actions/code.md) page.

## Your first C# code

Paste this into a **Run C# code** step:

```csharp
public class CPHInline
{
    public bool Execute()
    {
        CPH.SendMessage("Hello from my code!");
        return true;
    }
}
```

What each part means:

- `public class CPHInline` is the box that holds your code. Keep this name; Cuebot looks for it.
- `public bool Execute()` is where Cuebot starts. It runs every time the action runs.
- `CPH` is your toolbox. `CPH.SendMessage(...)` sends a chat message. See every tool in
  [C# methods (CPH)](../reference/csharp/index.md).
- `return true;` means "carry on with the next step". `return false;` stops the action here.

This is the same shape Streamer.bot uses, so you can paste Streamer.bot code as it is.

### Reading the action's variables

The trigger gives the action [variables](variables.md), like `%user%` (who did it). In C# they are in `args`:

```csharp
public class CPHInline
{
    public bool Execute()
    {
        // TryGetArg finds a variable and tells you if it was there
        if (CPH.TryGetArg("user", out string user))
        {
            CPH.SendMessage($"Welcome, {user}!");
        }
        return true;
    }
}
```

To give the next steps a new variable, use `CPH.SetArgument("winner", "Bob");`. The next steps can then use
`%winner%`.

### Remembering things: global variables

A global variable keeps a value between actions, and (when it is "persisted") after Cuebot restarts. Here is a
death counter for a `!death` command:

```csharp
public class CPHInline
{
    public bool Execute()
    {
        int deaths = CPH.GetGlobalVar<int>("deaths");
        deaths++;
        CPH.SetGlobalVar("deaths", deaths);
        CPH.SendMessage($"Deaths so far: {deaths}");
        return true;
    }
}
```

The same `deaths` value shows as `~deaths~` in any normal step, and on the **Variables** page.

### Remembering things inside the code

`CPHInline` is made once and kept while Cuebot runs. A field (a variable written outside `Execute`) keeps its value
between runs, until Cuebot closes:

```csharp
public class CPHInline
{
    private int _runs;

    public void Init()
    {
        // Runs once, before the first Execute
        CPH.LogInfo("My code is ready");
    }

    public bool Execute()
    {
        _runs++;
        CPH.LogInfo($"This code ran {_runs} times since Cuebot started");
        return true;
    }

    public void Dispose()
    {
        // Runs once, when Cuebot closes
    }
}
```

`Init()` and `Dispose()` are optional. Tick **Precompile on application start** on the step to run `Init()` when
Cuebot starts instead of before the first run.

### Short C# code

You can also skip the `CPHInline` box and write the steps directly. Then you get:

- `CPH`: the same toolbox;
- `Args` (or `args`): the action's variables;
- `Cb`: Cuebot's own helpers (below);
- `CancellationToken`: tells long work to stop when the step's time is up.

```csharp
var user = Args.TryGetValue("user", out var u) ? u?.ToString() : "friend";
await Cb.SendChat($"Hi {user}!");
var scene = await Cb.Obs("GetCurrentProgramScene");
await Cb.Log($"OBS shows {scene.GetProperty("currentProgramSceneName").GetString()}");
```

Here `return false;` stops the action too. Leaving out `return` carries on.

**`Cb` helpers in C#** (each one needs `await`):

| Helper | What it does |
| --- | --- |
| `Cb.Call(name, new { ... })` | Runs any [script function](../reference/script-functions/index.md) by name. Gives back its answer as JSON (`JsonElement`). |
| `Cb.Log(message, level)` | Writes a line in Cuebot's log. `level` is `"Info"` (the default), `"Warning"`, `"Error"` or `"Debug"`. || `Cb.GetGlobal(name)` | Reads a global variable as text, or `null` when it does not exist. |
| `Cb.SetGlobal(name, value, persisted)` | Saves a global variable. `persisted` (true at the start) keeps it after a restart. |
| `Cb.RunAction(name)` | Runs another action, waits for it, and gives back true when it worked. |
| `Cb.SendChat(message)` | Sends a message in your Twitch chat. |
| `Cb.Speak(text, alias)` | Says the text with text-to-speech. `alias` is a voice name; leave it out for your default voice. |
| `Cb.Obs(requestType, data)` | Sends one [obs-websocket request](https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md#requests) to OBS and gives back OBS's answer. |

### More C# facts

- Answers from `Cb.Call` (and `cb.call` in Python) name their fields with a small first letter, like `userName` and
  `isLive`, the same as the [REST API](../reference/rest-api.md). Read one with `answer.GetProperty("userName")`.
- C# code runs on .NET 10, the same as Cuebot. Old Windows-only parts (Windows Forms, `System.Web`) are not there.
- `Newtonsoft.Json` is ready to use: add `using Newtonsoft.Json;` at the top.
- Need another library? Put the full path of its `.dll` file in the step's **References** box, one per line.
- **Timeout (seconds)** stops code that runs too long (30 seconds at the start; 0 means no limit).
- **Run C# method** runs another method of a C# step that has a **Code name**. `CPH.ExecuteMethod("MyCode", "SayHi")` does the
  same from code.
- `CPH.TwitchOAuthToken` and `CPH.TwitchClientId` stop the script: Cuebot never gives your sign-in keys to code.
  [What Cuebot does not support](../reference/csharp/unsupported.md) lists every method that is there only so
  Streamer.bot code still compiles.

## Your first Python code

Paste this into a **Run Python code** step:

```python
user = cb.get_arg("user", "friend")
cb.send_chat(f"Hello {user}, this is Python!")
```

The first time, Cuebot downloads its own Python (version 3.13) and sets it up. That takes a minute; after that it is
quick.

- `cb` is your toolbox in Python.
- `print("text")` writes the text in the action's log. Handy for checking what your code does.
- `return False` stops the action. Anything else (or no `return`) carries on.
- When a Cuebot function fails, Python raises `CuebotError`. You can catch it:

```python
try:
    cb.obs_set_scene(scene_name="Gameplay")
except CuebotError as error:
    print(f"OBS said no: {error}")
```

**`cb` helpers in Python:**

| Helper | What it does |
| --- | --- |
| `cb.args` | The action's variables when the code started, as a dictionary. |
| `cb.get_arg(name, default)` | One variable, or `default` when it is not there. |
| `cb.set_arg(name, value)` | Sets a variable. Later steps can use it as `%name%`. |
| `cb.get_global(name, default)` | Reads a global variable, or `default` when it does not exist. |
| `cb.set_global(name, value, persisted=True)` | Saves a global variable (as text). |
| `cb.log(message, level="Info")` | Writes a line in Cuebot's log. |
| `cb.run_action(name)` | Runs another action, waits for it, and gives back `True` when it worked. |
| `cb.send_chat(message)` | Sends a message in your Twitch chat. |
| `cb.speak(text, alias=None)` | Says the text with text-to-speech. |
| `cb.obs(request_type, data=None)` | Sends one obs-websocket request to OBS and gives back OBS's answer. |
| `cb.call(name, **values)` | Runs any [script function](../reference/script-functions/index.md) by name, like `cb.call("speech.speak", text="Hi")`. |
| `cb.<area>_<function>(...)` | Every [script function](../reference/script-functions/index.md) as its own method, like `cb.speech_speak(text="Hi")` or `cb.vtubestudio_move_model(position_x=0.5)`. |

Every Cuebot function has its own `cb` method in Python, named in Python style: `vtubestudio.moveModel` becomes `cb.vtubestudio_move_model`, and its value `positionX` becomes `position_x`. Each function's page shows its Python name and values. `help(cb.speech_speak)` prints what a method does, and [cuebot.pyi](../reference/script-functions/cuebot.pyi) gives a code editor such as VS Code completion for all of them.

```python
# Walk the VTube Studio model across the screen with a little bounce
import math, time

for step in range(61):
    x = -1.2 + 2.4 * step / 60
    bounce = abs(math.sin(step / 60 * math.pi * 12)) * 0.05
    cb.vtubestudio_move_model(time=0, position_x=x, position_y=-0.5 + bounce)
    time.sleep(0.1)
```

A death counter in Python:

```python
deaths = int(cb.get_global("deaths", 0)) + 1
cb.set_global("deaths", deaths)
cb.send_chat(f"Deaths so far: {deaths}")
```

### Python packages and your own Python

**Automation > Scripting** sets Python up:

- **Install Python automatically the first time a Python step runs**: leave it ticked.
- **Advanced: use an existing Python interpreter instead**: type where your own Python is, if you want to use it.
- **Python packages**: type a package name, like `requests`, and click **Install**. Then `import requests` works in
  your code. **Remove** takes it away again.
- **Script API** lists every script function, the same list as the [script functions](../reference/script-functions/index.md)
  pages.

## Which toolbox should I use?

| You write | Use | Where the list is |
| --- | --- | --- |
| C# with `CPHInline` (Streamer.bot style) | `CPH.Something(...)` | [C# methods (CPH)](../reference/csharp/index.md) |
| Short C# | `CPH.Something(...)`, or `await Cb.Call("name", new { ... })` | [C# methods](../reference/csharp/index.md) and [script functions](../reference/script-functions/index.md) |
| Python | `cb.something(...)`, or `cb.call("name", ...)` | [Script functions](../reference/script-functions/index.md) |

**Script functions** are the same for both languages, and cover every part of Cuebot: OBS, chat, points, Stream
Deck, VTube Studio, and more. Each one shows a C# and a Python example.

## Talking to Cuebot from outside

Code steps run *inside* Cuebot. To control Cuebot from *another* program (a website, a script on another computer,
your own app), see [Controlling Cuebot from other programs](api.md).
