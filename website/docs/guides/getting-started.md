# Your first five minutes

By the end of this page, Cuebot will be signed in to Twitch, connected to OBS, and answering `!hello` in your chat.

## 1. Find your way around

On the far left is the **left rail**: a column of icons with short names. **Home**, **Automation**, **Chat**,
**Audio**, **Input**, **Platforms**, **Overlays**, **Apps** and, at the bottom, **Settings**. Click one, and its pages
are listed next to it.

!!! tip "How to read the paths on this site"
    When a page says **Automation > Actions**, it means: click **Automation** in the left rail, then **Actions** in
    the list next to it.

Two handy things:

- Press ++ctrl+k++ (or click "Jump to a page…" at the top) and type a page name, like "timers", to jump there.
- The top bar shows a small chip for each thing that is connected, like Twitch or OBS.

Cuebot saves your changes by itself, a moment after you make them. ++ctrl+s++ saves everything right away.

## 2. Sign in to Twitch

1. Go to **Platforms > Twitch** and open the **Accounts** tab.
2. Under **Broadcaster account** click **Sign in with Twitch**.
3. Your browser opens Twitch, and Cuebot shows a short code. Check that you are logged in to Twitch as **your
   streaming account**, then confirm the code on Twitch.
4. Back in Cuebot, the page now shows your account. Cuebot connects by itself.
5. Tick **Connect automatically on startup**, so you never have to do this again.

On YouTube, Kick or Rumble instead? See [Connecting your platforms](connecting-platforms.md). The rest of this page
works the same on every platform.

## 3. Connect OBS

First, in **OBS**:

1. Open **Tools > WebSocket Server Settings**.
2. Tick **Enable WebSocket server**.
3. Click **Show Connect Info**. Keep this window open: you need the port and the password.

Then, in **Cuebot**:

1. Go to **Apps > Stream apps** and open **OBS & Streamlabs Desktop**.
2. Under **OBS Studio**, check the connection. **Host** `127.0.0.1` and **Port** `4455` are right when OBS runs on the
   same computer. Copy the **Password** from OBS into the **Password** box.
3. Click **Connect**. The scenes from OBS show up under **Scenes and sources**.
4. Tick **Connect automatically and reconnect when OBS restarts**.

## 4. Your first chat command: `!hello`

You will make three small things:

- a **command**: the word viewers type, `!hello`;
- an **action**: what Cuebot does, here "write a message in chat";
- a **trigger**: the link that says "when the command is used, run the action".

!!! note "Cuebot already has an example `!hello`"
    The first time it starts, Cuebot makes a few example actions in a group called **Examples**, and one of them
    answers `!hello` on Twitch. Turn it off first, or your chat gets two answers: go to **Automation > Actions**, click
    the **!hello** action in the **Examples** group and click **Off**.

### Make the command

1. Go to **Automation > Commands**.
2. Click the **+** button (its tip says "Creates a new chat command, like !hello.").
3. In **Name**, type `hello`. This name is just for you.
4. In the big box under **Commands**, replace `!newcommand` with `!hello`.
5. Make sure **Enabled** is ticked.

### Make the action

1. Go to **Automation > Actions**.
2. Click the **+** button at the top of the list (its tip says "New action"). A new action called "New action" appears.
3. Click its name at the top and type a better one, like `Say hello`.

### Add the trigger

1. Find the **Triggers** list. On a wide window it is a column on the right. On a smaller window it is a
   **Triggers** tab next to **Sub-actions**.
2. Click **+ Trigger**.
3. In the search box type `command triggered`. Pick **Commands › Command triggered** and click **Add**.
4. The trigger's settings appear under the list. In **Command**, pick `hello`.

!!! warning "Do not leave Command empty"
    An empty **Command** box means "run for every command". That is handy later, but not now.

### Add what Cuebot should do

1. Open the **Sub-actions** tab and click **+ Sub-action**.
2. Type `send chat message`. Pick **Chat (all platforms) › Send chat message (any platform)** and click **Add**.
3. In **Message**, type:

    ```text
    Hello %user%, welcome in!
    ```

    `%user%` is a [variable](variables.md): Cuebot swaps it for the name of the person who typed the command.

4. Tick **Reply on the platform the trigger came from**, so a Twitch viewer gets the answer on Twitch.

### Try it

- **In your chat:** type `!hello`. Cuebot answers "Hello YourName, welcome in!".
- **Without chat:** click **Test trigger…** (or press ++f5++). The **Arguments** box already says `user=TestUser`.
  Change it to `user=Bob` if you like, and click **Run test**. Cuebot runs the action as if Bob had typed the
  command.

!!! note "A test is real"
    **Run** and **Run test** really do what the action says. Here, that means a real message in your chat.

## 5. It did not answer?

Check these, in order:

1. Is the platform connected? Look for its chip in the top bar.
2. Is the **command** ticked **Enabled** on the Commands page?
3. Is the **action** **On**, and is the trigger's switch on?
4. Does the trigger's **Command** say `hello`?
5. Open **Chat > History**. Every run is listed there. If your action is missing, the trigger did not fire. If it is
   there, click it to see what happened.
6. Open **Home > Dashboard** and the **Log** tab. Warnings show up there, like a missing Twitch permission.

## What next?

- [How actions work](actions-and-triggers.md): add more steps, like switching a scene or playing a sound.
- [Chat commands](commands.md): cooldowns, who may use a command, and `!so bob`-style commands.
- [Overlays and alerts](overlays.md): show alerts on your stream.
- Stuck? The [setup assistant](assistant.md) can do the clicking for you.
