# The assistant on your own computer

The [setup assistant](assistant.md) normally borrows a big AI from a company (ChatGPT, Claude, Grok, Gemini). It can
also use a **local model**: a small AI that runs on your own computer, through a free program called **Ollama** (or
**LM Studio**).

| | Big AI (AI app or API key) | Local model |
| --- | --- | --- |
| Cost | A subscription or pay per use | Free |
| Privacy | Your messages go to the AI company | Nothing leaves your computer |
| Gets it right | Almost always | **Often, but not always** |
| Speed | A few seconds | Seconds with a graphics card, **minutes without one** |
| Needs | An account | A few GB of disk space; ideally a graphics card |

!!! warning "Be honest with yourself about what to expect"
    A local model is a small brain. It is good at questions ("what does my Raid alert do?") and simple builds ("make a
    !lurk command"). It gets bigger jobs wrong much more often: it may make the command but forget to connect the
    action, pick the wrong step, or say "done" when it is not. **Always check what it made** on the Actions and
    Commands pages. For big setup jobs, a big AI is the better tool.

## Which model to use

Cuebot was tested with these. "Memory" is the graphics card memory (VRAM) the model needs.

| Your computer | Model | Download | Memory | How it did in Cuebot's tests |
| --- | --- | --- | --- | --- |
| Graphics card with **8 GB or more** | **Qwen3.5 9B** (`qwen3.5:9b`) | 6.6 GB | about 6.2 GB | Right 14 times out of 15 |
| Graphics card with **4 to 6 GB**, or **no graphics card** | **Qwen3.5 4B** (`qwen3.5:4b`) | 3.3 GB | about 3.8 GB | Right 8 times out of 15: fine for questions and simple builds, often wrong on bigger ones |

Other models also work if they can "call tools" (Ollama marks them with **tools**). Gemma 4 E4B came third in the
tests. Very small models (3B and below) were not reliable enough to recommend.

**No graphics card?** It works, but slowly: a simple build took about half a minute to several minutes on a fast
desktop processor, and longer on a laptop. Use the 4B model, and be patient.

## Setting it up

1. **Install Ollama** from [ollama.com](https://ollama.com) (Windows, Linux and Mac). It starts by itself and runs in
   the background.
2. **Download a model.** Open a terminal (on Windows: PowerShell) and type one of these, then wait for it to finish:

    ```
    ollama pull qwen3.5:9b
    ```

    ```
    ollama pull qwen3.5:4b
    ```

3. In Cuebot, go to **Home > Assistant** and pick **Local model (Ollama, LM Studio)** in **AI**. The dot turns green
   when Cuebot finds Ollama and a model.
4. Leave **Model** empty to let Cuebot pick (it prefers Qwen3.5), or pick one from the list.

**Using LM Studio instead?** Start its server (Developer tab, or `lms server start`), load a model, set its context
length to at least 16384, and type `http://127.0.0.1:1234` in **Server**.

## The options

- **Server**: where Ollama or LM Studio listens. Leave it empty for Ollama on this computer.
- **Memory (tokens)**: how much of the conversation the model can keep in mind (its "context"). **32768** is right for
  most people. Lower it only if your graphics card runs out of memory; below about 16000, Cuebot's tools no longer fit.
- **Let it think first**: off by default. Thinking made the models much slower (a simple build went from about half a
  minute to five minutes without a graphics card) and did not make them more accurate in Cuebot's tests.
- **Give it every tool**: off by default. Small models get Cuebot's tools for actions, commands and timers, plus a way
  to find the others (overlays, polls, points and so on) when they need them. With all 117 tools at once, small models
  got confused and failed about twice as often. Tick it only for big models (30B and up).

Changes apply from the next **New chat**.

## Problems you may see

| What happens | Why | What to do |
| --- | --- | --- |
| It says "done" but the command does nothing | It made the command but did not connect an action to it, or used the wrong trigger | Check the action's **Triggers**; tell it what is missing ("the !lurk command has no action, add one") |
| It asks you to type "yes" before deleting | Small models sometimes ask in words instead of using the **Approve** button | Answer "yes"; Cuebot still shows **Approve** before anything is deleted |
| It changes something you did not ask for | It misread the request | Change it back on the Actions page (undo there does not cover the assistant's changes), and ask again more simply. Before a big job, a backup (**Settings > Backups**) is a good idea |
| An empty answer, or "the model server returned an error" | Small models sometimes stop or write a broken request. Cuebot already retries once and nudges once | Send the message again, or press **New chat** |
| "The conversation has filled this model's memory" | Long conversations do not fit | Press **New chat**, or raise **Memory** if your graphics card has room |
| Very slow answers | No graphics card, or the model does not fit in its memory | Use the 4B model, close games and other heavy programs, keep **Let it think first** off |
| "Ollama is not running" | Ollama is closed | Start Ollama (on Windows, from the Start menu) and press **Refresh** |
| "Ollama is running but has no models yet" | No model downloaded | Run `ollama pull qwen3.5:4b` and press **Refresh** |

**Tips for better results**

- Ask for **one thing at a time**: "make a !pausealerts command for moderators", then "make it pause the alerts and
  the speech".
- Use the **exact names** you see in Cuebot ("the action called Raid alert").
- Start a **New chat** for each new job, so the model's memory stays clear.

## How Cuebot tested these models

Cuebot sent typical requests to each model through the real assistant, five times each, and checked what was actually
built instead of trusting the answer (October 2026, Ollama 0.40.13, RTX 4090):

| Request | Qwen3.5 9B | Qwen3.5 4B |
| --- | --- | --- |
| "Make a !lurk command that thanks the viewer by name" | 5 of 5 | 3 of 5 |
| "Moderator-only !pausealerts and !resumealerts that pause and resume alerts and text-to-speech" | 4 of 5 | 1 of 5 |
| "Every 15 minutes, post my socials in chat" | 5 of 5 | 4 of 5 |

Each took a few seconds with a graphics card. Questions about your setup ("which actions use a chat command?") and
deleting something (with **Approve**) also worked in Cuebot's tests. New models come out often; if you try another
one, check its results the same way. The tests are part of Cuebot's source code (docs/ASSISTANT.md explains how to
run them).
