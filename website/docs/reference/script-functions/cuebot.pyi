# Cuebot's `cb` object for Python scripts, for code editors: put this file next to your script to get
# completion and help for every Cuebot function. Cuebot itself does not need it.
from typing import Any


class CuebotError(Exception):
    """Raised when a Cuebot function fails."""


class StopAction(Exception):
    """Raised by cb.run_step when the step stopped the action."""


class Cuebot:
    @property
    def args(self) -> dict[str, Any]:
        """A snapshot of the action's arguments when the script started (updated by set_arg)."""
    def call(self, function: str, /, **params: Any) -> Any:
        """Calls any Cuebot script function by name, like cb.call("speech.speak", text="Hi")."""
    def get_arg(self, name: str, default: Any = None) -> Any:
        """One of the action's arguments, or default when it is not there."""
    def set_arg(self, name: str, value: Any) -> None:
        """Sets an argument; later steps can use it as %name%."""
    def get_global(self, name: str, default: Any = None) -> Any:
        """Reads a global variable, or default when it does not exist."""
    def set_global(self, name: str, value: Any, persisted: bool = True) -> None:
        """Saves a global variable (as text)."""
    def log(self, message: Any, level: str = "Info") -> None:
        """Writes a line in Cuebot's log."""
    def run_action(self, name: str) -> bool:
        """Runs another action and waits for it; returns whether it succeeded."""
    def send_chat(self, message: Any) -> None:
        """Sends a message in your Twitch chat."""
    def speak(self, text: Any, alias: str | None = None) -> None:
        """Says the text with text-to-speech."""
    def obs(self, request_type: str, data: Any = None) -> Any:
        """Sends an obs-websocket request and returns its response data."""
    def run_step(self, type: str, params: dict[str, Any] | None = None) -> str | None:
        """Runs one step like the action editor does; returns the branch it picked, or None. Raises StopAction when it stops the action."""
    def run_steps(self, steps: list[dict[str, Any]] | str) -> str:
        """Runs steps (with the steps inside them) in order; returns "completed", "stop", "break" or "continue"."""
    def check(self, left: Any, operator: str, right: Any = "") -> bool:
        """Checks a condition the way the If step does."""
    def resolve(self, text: Any) -> str:
        """Fills in %variables% and ~globals~ in a text, the way step settings are."""
    def pick(self, *weights: float) -> int:
        """Picks a position at random by weight (0 = first), like a random group."""
    def actions_run(self, name: Any) -> Any:
        """Runs another action by its name and waits until it is done. It gets a copy of this action's variables. Fails when no action has that name.

        name: The action's name as shown on the Actions page, like \"Shoutout\". Upper or lower case does not matter.
        Gives back: true when the action finished without errors, otherwise false.
        Same as cb.call(\"actions.run\", ...).
        """
    def args_all(self) -> Any:
        """Gives you every variable of the running action at once, like user, message and rewardTitle.

        Gives back: An object with every variable name and its value.
        Same as cb.call(\"args.all\", ...).
        """
    def args_get(self, name: Any) -> Any:
        """Reads one variable of the running action, like the user who triggered it.

        name: The variable name without % signs, like \"user\".
        Gives back: The value, or null when the action has no variable with that name.
        Same as cb.call(\"args.get\", ...).
        """
    def args_resolve(self, text: Any) -> Any:
        """Fills in %variables% and ~globals~ in a text, exactly like a step's settings are filled in. Handy for text you copied from a step.

        text: The text, like \"Thanks %user% for %bits% bits!\".
        Gives back: The text with the variables filled in. A %variable% that does not exist stays as it is.
        Same as cb.call(\"args.resolve\", ...).
        """
    def args_set(self, name: Any, value: Any) -> Any:
        """Sets a variable of the running action. Later sub-actions can use it as %name%.

        name: The variable name without % signs, like \"winner\".
        value: The value to store: text, a number, true/false or a list. Leave it out to set it to empty (null).
        Gives back: Nothing.
        Same as cb.call(\"args.set\", ...).
        """
    def controllers_get(self, *, controller: Any = None) -> Any:
        """Finds one game controller and tells you about it. Starts watching controllers if needed.

        controller: Optional. Which controller: a slot like \"#1\", part of its name, its type (like \"PS5\") or its GUID. Default \"#1\".
        Gives back: The controller's details (name, slot, type, battery, held buttons, axes, hats), or null when none matches.
        Same as cb.call(\"controllers.get\", ...).
        """
    def controllers_learn(self, *, timeout_ms: Any = None, controller: Any = None) -> Any:
        """Waits for you to press a button, move a stick or push the d-pad, then tells you what it was. Handy for finding a button's name.

        timeout_ms: Optional. How long to wait, in milliseconds (100 to 300000). Default 10000 (10 seconds).
        controller: Optional. Only listen to this controller (slot like \"#1\", name, type or GUID). Leave it out to listen to all.
        Gives back: What was pressed: kind, name, label, controller (its details), triggerType, settings and description. null when nothing was pressed in time.
        Same as cb.call(\"controllers.learn\", ...).
        """
    def controllers_list(self) -> Any:
        """Lists the game controllers that are plugged in right now. Starts watching controllers if it was not already. Fails with an error when controller input is turned off.

        Gives back: A list of controllers. Each has its name, slot number, type, battery, held buttons, axes (stick and trigger values) and hats (d-pad directions).
        Same as cb.call(\"controllers.list\", ...).
        """
    def controllers_reload_mappings(self) -> Any:
        """Reads the controller mappings file (gamecontrollerdb.txt) again, so new controller types are known. Fails with an error when there is no such file.

        Gives back: How many mappings were added. 0 when controller input is not running yet (the file is read when it starts).
        Same as cb.call(\"controllers.reloadMappings\", ...).
        """
    def controllers_rumble(self, *, controller: Any = None, low: Any = None, high: Any = None, duration_ms: Any = None) -> Any:
        """Makes a game controller shake (rumble). Fails with an error when controller input is turned off.

        controller: Optional. Which controller: a slot like \"#1\", part of its name, its type or GUID. Several can be split with commas. Default \"Any\" (all of them).
        low: Optional. Strength of the big, slow motor, 0-100 percent. Default 50.
        high: Optional. Strength of the small, fast motor, 0-100 percent. Default 50.
        duration_ms: Optional. How long to shake, in milliseconds (1000 = 1 second, at most 60000). Default 500.
        Gives back: How many controllers shook.
        Same as cb.call(\"controllers.rumble\", ...).
        """
    def controllers_set_led(self, color: Any, *, controller: Any = None) -> Any:
        """Changes the light colour on game controllers that have one, like a PlayStation controller. Fails with an error when the colour cannot be read.

        color: The colour as \"#RRGGBB\" (like \"#FF0000\" for red) or \"r,g,b\" (like \"255,0,0\").
        controller: Optional. Which controller: a slot like \"#1\", part of its name, its type or GUID. Default \"Any\" (all of them).
        Gives back: How many controllers changed colour.
        Same as cb.call(\"controllers.setLed\", ...).
        """
    def controllers_status(self) -> Any:
        """Tells you whether game controller input is running and how.

        Gives back: running (true/false), backend (the controller library in use), version, state, message (a status line) and mode (the watch setting from the Controllers page).
        Same as cb.call(\"controllers.status\", ...).
        """
    def credits_add(self, section: Any, *, user: Any = None, platform: Any = None, amount: Any = None, detail: Any = None) -> Any:
        """Adds a name to your end-of-stream credits, in any section. A name that is already in the section gets its count and amount raised.

        section: The section: a built-in one like \"follows\", \"raids\" or \"tips\", or your own name like \"Special thanks\".
        user: Optional. The name to show, like \"CoolViewer42\". Leave it out for the user of the trigger.
        platform: Optional. The platform, like \"twitch\". Leave it out for the platform of the trigger.
        amount: Optional. A number to add up, like 5 for a tip of 5. Leave it out for 0.
        detail: Optional. Extra text shown with the name, like \"Tier 1\".
        Gives back: true. Fails when the section or the user is empty.
        Same as cb.call(\"credits.add\", ...).
        """
    def credits_get(self) -> Any:
        """Gives you the credits collected so far this stream, for your own credits overlay or a chat message.

        Gives back: An object with startedAt, updatedAt and sections. Each section has id, title and entries; each entry has user, platform, count, amount and detail.
        Same as cb.call(\"credits.get\", ...).
        """
    def credits_reset(self) -> Any:
        """Clears the credits, so the next stream starts empty.

        Gives back: true.
        Same as cb.call(\"credits.reset\", ...).
        """
    def credits_test(self) -> Any:
        """Gives you made-up sample credits, so you can design a credits overlay without waiting for a real stream. Your real credits do not change.

        Gives back: Sample credits in the same shape as credits.get (with isTest set to true).
        Same as cb.call(\"credits.test\", ...).
        """
    def crowdcontrol_get_session(self) -> Any:
        """Tells you about the Crowd Control game session that is running, and the timed effects that are active.

        Gives back: active (true while a session runs), paused, gameSessionId, gameName, gamePackName and timedEffects (a list: requestId, effectName, user, status, timeRemaining in milliseconds).
        Same as cb.call(\"crowdcontrol.getSession\", ...).
        """
    def crowdcontrol_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Crowd Control right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"crowdcontrol.isConnected\", ...).
        """
    def crowdcontrol_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a Crowd Control event inside Cuebot, so you can test your triggers. Nothing is sent to Crowd Control. Other kinds: effectFailure, effectRefund, timedEffectUpdated, timedEffectEnded, gameSessionEnd.

        kind: Optional. Which event, like \"effectSuccess\" (default), \"effectRequest\", \"timedEffectStarted\", \"coinExchange\" or \"gameSessionStart\".
        Gives back: true when the test event was sent, false when the kind is unknown.
        Same as cb.call(\"crowdcontrol.sendTestEvent\", ...).
        """
    def desktop_get_clipboard(self) -> Any:
        """Reads the text that is on your clipboard right now. Fails with an error when this computer has no clipboard Cuebot can use.

        Gives back: The clipboard text, or null when the clipboard holds no text.
        Same as cb.call(\"desktop.getClipboard\", ...).
        """
    def desktop_notify(self, message: Any, *, title: Any = None, icon: Any = None, id: Any = None, duration: Any = None) -> Any:
        """Shows a pop-up notification on your computer. Cuebot uses the system's notifications where it can (Linux), else its own pop-up in the app.

        message: The text under the title, like \"CoolViewer just followed\".
        title: Optional. The top line, like \"New follower!\". Default \"Cuebot\".
        icon: Optional. The path to an image file to show with it.
        id: Optional. A name for this notification. The \"Toast notification clicked\" trigger tells you this name when it is clicked.
        duration: Optional. How many seconds it stays on screen, 1 to 120. Default 5.
        Gives back: Where it was shown: \"system\" or \"in-app\". Fails with an error when no way to show it is available.
        Same as cb.call(\"desktop.notify\", ...).
        """
    def desktop_set_clipboard(self, text: Any) -> Any:
        """Puts text on your clipboard, as if you copied it. Fails with an error when this computer has no clipboard Cuebot can use.

        text: The text to copy, like \"Thanks for watching!\"
        Gives back: true when it worked.
        Same as cb.call(\"desktop.setClipboard\", ...).
        """
    def donordrive_get_profile(self) -> Any:
        """Gives you the last details Cuebot read from your DonorDrive page, like your goal and how much you raised.

        Gives back: id, name, teamName, eventName, goal, raised, donations (how many), isTeam (true/false) and url. null when nothing was read yet.
        Same as cb.call(\"donordrive.getProfile\", ...).
        """
    def donordrive_is_connected(self) -> Any:
        """Checks whether Cuebot is watching your DonorDrive (charity) page right now.

        Gives back: true while Cuebot is checking your participant or team page, otherwise false.
        Same as cb.call(\"donordrive.isConnected\", ...).
        """
    def donordrive_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a DonorDrive event inside Cuebot, so you can test your triggers. Nothing is sent to DonorDrive and it works without a connection.

        kind: Optional. Which event: \"donation\", \"incentive\" (a donation that picked a reward) or \"profile\" (your totals went up). Default \"donation\".
        Gives back: true when the test event was sent, false when the kind is unknown.
        Same as cb.call(\"donordrive.sendTestEvent\", ...).
        """
    def elgato_camera_hub_is_available(self) -> Any:
        """Checks whether Cuebot can control webcam settings on this computer. Right now this works on Linux.

        Gives back: true when webcam control works here, otherwise false.
        Same as cb.call(\"elgato.cameraHub.isAvailable\", ...).
        """
    def elgato_camera_hub_set(self, control: Any, value: Any, *, device: Any = None, adjust: Any = None) -> Any:
        """Changes one picture setting on your webcam, like brightness or zoom. Fails with an error when the webcam or setting is not found.

        control: The setting: brightness, contrast, saturation, sharpness, focus, pan, tilt, whiteBalance, zoom, autoFocus, autoWhiteBalance, autoExposure, antiFlicker, mirror or flip.
        value: The new value as a number, like 60. Zoom is 100-400 percent. On/off settings take 1 (on) or 0 (off). antiFlicker also takes \"Off\", \"50 Hz\", \"60 Hz\" or \"Auto\".
        device: Optional. Which webcam: its name, id or device path. Leave it out (or empty) for the first webcam. \"All\" means every webcam.
        adjust: Optional. true adds the value to the current one instead. Default false.
        Gives back: The new value of the setting (from the first webcam).
        Same as cb.call(\"elgato.cameraHub.set\", ...).
        """
    def elgato_camera_hub_webcams(self) -> Any:
        """Lists your webcams and their current picture settings.

        Gives back: A list of webcams. Each has deviceId, deviceName, devicePath and the settings it supports, like brightness, contrast, focus, zoomValue, autoFocus, antiFlicker, mirrored and flipped.
        Same as cb.call(\"elgato.cameraHub.webcams\", ...).
        """
    def elgato_lights_add(self, address: Any) -> Any:
        """Adds an Elgato light by its network address. Handy when Cuebot does not find it on its own. Fails with an error when the light cannot be reached.

        address: The light's IP address, like \"192.168.1.50\". You can add a port, like \"192.168.1.50:9123\". Find it in the Elgato Control Center app.
        Gives back: The light that was added, with the same details as elgato.lights.list.
        Same as cb.call(\"elgato.lights.add\", ...).
        """
    def elgato_lights_discover(self) -> Any:
        """Looks for Elgato lights on your network right now.

        Gives back: A list of the lights that were found (same details as elgato.lights.list).
        Same as cb.call(\"elgato.lights.discover\", ...).
        """
    def elgato_lights_identify(self, device: Any) -> Any:
        """Makes Elgato lights blink, so you can see which one is which. Fails with an error when no light matches.

        device: Which light: its name, id or IP address. Split several with commas. \"All\" or empty means every light.
        Gives back: true when done.
        Same as cb.call(\"elgato.lights.identify\", ...).
        """
    def elgato_lights_is_running(self) -> Any:
        """Checks whether Cuebot is watching your Elgato lights right now.

        Gives back: true while Cuebot checks your lights, otherwise false.
        Same as cb.call(\"elgato.lights.isRunning\", ...).
        """
    def elgato_lights_list(self) -> Any:
        """Lists every Elgato light Cuebot knows, like a Key Light or Light Strip.

        Gives back: A list of lights. Each has deviceId, deviceName, productName, serialNumber, ipAddress, port and online. Lights that answered also have on, brightness, temperature (Kelvin), hue and saturation.
        Same as cb.call(\"elgato.lights.list\", ...).
        """
    def elgato_lights_rename(self, device: Any, name: Any) -> Any:
        """Gives an Elgato light a new name. The name is saved on the light itself, so Control Center shows it too.

        device: Which light: its name, id or IP address.
        name: The new name, like \"Desk Light\".
        Gives back: true when done.
        Same as cb.call(\"elgato.lights.rename\", ...).
        """
    def elgato_lights_set_brightness(self, device: Any, brightness: Any, *, adjust: Any = None) -> Any:
        """Changes how bright Elgato lights are. Fails with an error when no light matches.

        device: Which light: its name, id or IP address. Split several with commas. \"All\" or empty means every light.
        brightness: How bright, 0-100 percent, like 75.
        adjust: Optional. true adds the number to the current brightness instead (use a minus number to dim). Default false.
        Gives back: A list of the lights that changed, with their new details.
        Same as cb.call(\"elgato.lights.setBrightness\", ...).
        """
    def elgato_lights_set_color(self, device: Any, hue: Any, saturation: Any) -> Any:
        """Changes the colour of Elgato lights that can show colours, like the Light Strip. Fails with an error when no light matches.

        device: Which light: its name, id or IP address. Split several with commas. \"All\" or empty means every light.
        hue: The colour, 0-360 around the colour wheel: 0 red, 120 green, 240 blue.
        saturation: How strong the colour is, 0-100. 0 is white, 100 is full colour.
        Gives back: A list of the lights that changed, with their new details.
        Same as cb.call(\"elgato.lights.setColor\", ...).
        """
    def elgato_lights_set_power(self, device: Any, *, state: Any = None) -> Any:
        """Turns Elgato lights on or off. Fails with an error when no light matches.

        device: Which light: its name, id or IP address. Split several with commas. \"All\" or empty means every light.
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch to the other one). Default \"Toggle\".
        Gives back: A list of the lights that changed, with their new details.
        Same as cb.call(\"elgato.lights.setPower\", ...).
        """
    def elgato_lights_set_temperature(self, device: Any, temperature: Any, *, adjust: Any = None) -> Any:
        """Changes the colour warmth of Elgato lights, from warm yellow to cool white. Fails with an error when no light matches.

        device: Which light: its name, id or IP address. Split several with commas. \"All\" or empty means every light.
        temperature: The warmth in Kelvin, 2900 (warm) to 7000 (cool), like 4500.
        adjust: Optional. true adds the Kelvin number to the current value instead. Default false.
        Gives back: A list of the lights that changed, with their new details.
        Same as cb.call(\"elgato.lights.setTemperature\", ...).
        """
    def elgato_lights_start(self) -> Any:
        """Starts looking for Elgato lights on your network and watching them. It starts over if it was already running.

        Gives back: true once it has started.
        Same as cb.call(\"elgato.lights.start\", ...).
        """
    def elgato_studio_is_running(self) -> Any:
        """Checks whether the Elgato Studio app is open on this computer. Cuebot can only see it; Elgato Studio has no way to be controlled.

        Gives back: true while Elgato Studio is running, otherwise false.
        Same as cb.call(\"elgato.studio.isRunning\", ...).
        """
    def emotes_find(self, *, platform: Any = None, message: Any = None) -> Any:
        """Finds the emotes in a chat message, from the platform itself and from 7TV, BTTV and FFZ. Handy for emote rain or counting emotes.

        platform: Optional. Where the message is from: \"Twitch\", \"YouTube\", \"Kick\" or \"Rumble\". Leave it out for the platform of the trigger (or Twitch).
        message: Optional. The message text, like \"hello KEKW\". Leave it out for the message of the trigger.
        Gives back: A list. Each emote has start and length (where it sits in the text), code (like \"KEKW\"), provider, imageUrl and zeroWidth.
        Same as cb.call(\"emotes.find\", ...).
        """
    def emotes_list(self, *, platform: Any = None) -> Any:
        """Lists every emote code Cuebot knows for a platform: 7TV, BTTV and FFZ emotes, plus platform emotes it has seen in chat.

        platform: Optional. \"Twitch\", \"YouTube\", \"Kick\" or \"Rumble\". Leave it out for Twitch.
        Gives back: A list of emote codes as text, like [\"KEKW\", \"catJAM\"], sorted A to Z.
        Same as cb.call(\"emotes.list\", ...).
        """
    def emotes_refresh(self, *, force: Any = None) -> Any:
        """Downloads the 7TV, BTTV and FFZ emote sets again, so new emotes show up. Only providers that are turned on are loaded.

        force: Optional. true (the default) always downloads fresh sets; false uses copies Cuebot saved recently.
        Gives back: How many emotes Cuebot knows now.
        Same as cb.call(\"emotes.refresh\", ...).
        """
    def globals_clear_temporary(self) -> Any:
        """Deletes every temporary global variable (the ones that are not saved). Saved ones stay.

        Gives back: How many variables were deleted.
        Same as cb.call(\"globals.clearTemporary\", ...).
        """
    def globals_delete(self, name: Any) -> Any:
        """Deletes a global variable.

        name: The variable name, like \"deaths\".
        Gives back: true when the variable existed, false when there was nothing to delete.
        Same as cb.call(\"globals.delete\", ...).
        """
    def globals_get(self, name: Any) -> Any:
        """Reads a global variable as text, the same way ~name~ shows it in a message.

        name: The variable name, like \"deaths\".
        Gives back: The value as text, or null when there is no such variable.
        Same as cb.call(\"globals.get\", ...).
        """
    def globals_get_value(self, name: Any) -> Any:
        """Reads a global variable and keeps its type, so a number stays a number and true/false stays true/false.

        name: The variable name, like \"deaths\".
        Gives back: The value as a number, true/false or text, or null when there is no such variable.
        Same as cb.call(\"globals.getValue\", ...).
        """
    def globals_increment(self, name: Any, *, amount: Any = None, persisted: Any = None) -> Any:
        """Adds to a number in a global variable, like a death counter. A missing variable starts at 0. Fails when the variable holds text that is not a number.

        name: The variable name, like \"deaths\".
        amount: Optional. How much to add, like 1 or 0.5. Use a negative number to subtract. Leave it out to add 1.
        persisted: Optional. true (the default) saves it so it is still there after a restart; false keeps it only until Cuebot closes.
        Gives back: The new value.
        Same as cb.call(\"globals.increment\", ...).
        """
    def globals_set(self, name: Any, value: Any, *, persisted: Any = None) -> Any:
        """Saves a value in a global variable. Every action can read it, and messages can show it with ~name~. The value is stored as text.

        name: The variable name, like \"lastRaider\".
        value: The value to store, like \"CoolStreamer\". Numbers and true/false are stored as text.
        persisted: Optional. true (the default) saves it so it is still there after a restart; false keeps it only until Cuebot closes.
        Gives back: Nothing.
        Same as cb.call(\"globals.set\", ...).
        """
    def goals_add_points(self, points: Any, *, goal: Any = None, use_multiplier: Any = None) -> Any:
        """Adds points to a goal, or takes them away with a negative number.

        points: How many points.
        goal: Optional. The goal's name. Leave it out for the first goal.
        use_multiplier: Optional. true to use the goal's multiplier.
        Gives back: The points really added.
        Same as cb.call(\"goals.addPoints\", ...).
        """
    def goals_get(self, *, goal: Any = None) -> Any:
        """Gives you a goal: its points, steps and settings.

        goal: Optional. The goal's name. Leave it out for the first goal.
        Gives back: An object with id, name, points, steps (each with name and points, lowest first), reachedThrough (the highest step already announced), multiplier, resetOnNewStream and values, or null when there is no such goal.
        Same as cb.call(\"goals.get\", ...).
        """
    def goals_list(self) -> Any:
        """Gives you every goal.

        Gives back: A list of goals, each like goals.get gives.
        Same as cb.call(\"goals.list\", ...).
        """
    def goals_reset(self, *, goal: Any = None) -> Any:
        """Puts a goal back to 0 points, so every step can be reached again.

        goal: Optional. The goal's name. Leave it out for the first goal.
        Gives back: true.
        Same as cb.call(\"goals.reset\", ...).
        """
    def goals_set_multiplier(self, multiplier: Any, *, goal: Any = None) -> Any:
        """Changes how much event points count. Points already added do not change.

        multiplier: 2 = double, 1 = normal, 0.5 = half.
        goal: Optional. The goal's name. Leave it out for the first goal.
        Gives back: true.
        Same as cb.call(\"goals.setMultiplier\", ...).
        """
    def goals_set_points(self, points: Any, *, goal: Any = None) -> Any:
        """Sets a goal's points.

        points: The new number of points.
        goal: Optional. The goal's name. Leave it out for the first goal.
        Gives back: true.
        Same as cb.call(\"goals.setPoints\", ...).
        """
    def goxlr_command(self, command: Any, *, serial: Any = None) -> Any:
        """Sends a raw command to the GoXLR Utility. For advanced users who know the GoXLR Utility's command names.

        command: The command, like {\"SetFXEnabled\": true}. You can also give it as JSON text.
        serial: Optional. The serial number of the GoXLR to send it to. Leave it out for your main GoXLR.
        Gives back: true when done.
        Same as cb.call(\"goxlr.command\", ...).
        """
    def goxlr_connect(self) -> Any:
        """Starts connecting to the GoXLR Utility. It does not wait: Cuebot keeps trying in the background until it works.

        Gives back: true once connecting has started (not when it is connected).
        Same as cb.call(\"goxlr.connect\", ...).
        """
    def goxlr_disconnect(self) -> Any:
        """Disconnects Cuebot from the GoXLR Utility.

        Gives back: true when done.
        Same as cb.call(\"goxlr.disconnect\", ...).
        """
    def goxlr_get_raw_status(self) -> Any:
        """Gives you everything the GoXLR Utility reports, exactly as it sends it. For advanced scripts.

        Gives back: The utility's whole status as JSON text. Empty text when nothing was received yet.
        Same as cb.call(\"goxlr.getRawStatus\", ...).
        """
    def goxlr_get_status(self) -> Any:
        """Tells you about the GoXLR Utility connection and your GoXLR: profiles, faders, volumes, mutes and effects.

        Gives back: isConnected, status (a status line), url, daemonVersion, devices (each GoXLR with its profile, faders, volumes in percent, cough button, effects and sampler bank), profiles, micProfiles and presets.
        Same as cb.call(\"goxlr.getStatus\", ...).
        """
    def goxlr_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to the GoXLR Utility right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"goxlr.isConnected\", ...).
        """
    def goxlr_load_mic_profile(self, name: Any, *, remember: Any = None) -> Any:
        """Loads one of your GoXLR mic profiles. Fails with an error when the GoXLR Utility is not connected.

        name: The mic profile name as the GoXLR Utility shows it, like \"Dynamic Mic\".
        remember: Optional. true makes the GoXLR Utility load this mic profile again next time it starts. Default true.
        Gives back: true when done.
        Same as cb.call(\"goxlr.loadMicProfile\", ...).
        """
    def goxlr_load_profile(self, name: Any, *, remember: Any = None) -> Any:
        """Loads one of your GoXLR profiles. Fails with an error when the GoXLR Utility is not connected.

        name: The profile name as the GoXLR Utility shows it, like \"Streaming\".
        remember: Optional. true makes the GoXLR Utility load this profile again next time it starts. Default true.
        Gives back: true when done.
        Same as cb.call(\"goxlr.loadProfile\", ...).
        """
    def goxlr_play_sample(self, button: Any, *, bank: Any = None, index: Any = None) -> Any:
        """Plays a sample from a sampler button, as if you pressed it. The GoXLR Mini has no sampler, so it fails with an error there.

        button: The button: \"TopLeft\", \"TopRight\", \"BottomLeft\" or \"BottomRight\".
        bank: Optional. The bank: \"A\", \"B\" or \"C\". Leave it empty for the bank that is active now.
        index: Optional. Which sample on that button, counting from 1. Leave it out to play the button's next sample.
        Gives back: bank and button that were played.
        Same as cb.call(\"goxlr.playSample\", ...).
        """
    def goxlr_request(self, request: Any) -> Any:
        """Sends any raw request to the GoXLR Utility and gives back its answer. For advanced users.

        request: The request, like \"GetStatus\" or {\"Command\": [...]}. You can also give it as JSON text.
        Gives back: The GoXLR Utility's answer data, like \"Ok\" or the status object.
        Same as cb.call(\"goxlr.request\", ...).
        """
    def goxlr_set_cough_mute(self, *, state: Any = None) -> Any:
        """Mutes or unmutes your mic with the cough button (on a GoXLR Mini: the mic mute button). Fails with an error when the GoXLR Utility is not connected.

        state: Optional. \"Mute\", \"Mute to all\", \"Unmute\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new mute state: \"Unmuted\", \"MutedToX\" or \"MutedToAll\".
        Same as cb.call(\"goxlr.setCoughMute\", ...).
        """
    def goxlr_set_effect(self, effect: Any, *, state: Any = None) -> Any:
        """Turns a voice effect on or off. The GoXLR Mini has no effects, so it fails with an error there.

        effect: The effect: \"FX\", \"Megaphone\", \"Robot\" or \"Hard Tune\".
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"goxlr.setEffect\", ...).
        """
    def goxlr_set_effect_preset(self, preset: Any) -> Any:
        """Picks one of the six effect presets. The GoXLR Mini has no effects, so it fails with an error there.

        preset: The preset number 1-6, like \"3\", or its name.
        Gives back: The preset that is now active, like \"Preset3\".
        Same as cb.call(\"goxlr.setEffectPreset\", ...).
        """
    def goxlr_set_fader_channel(self, fader: Any, channel: Any) -> Any:
        """Puts a different channel on one of the GoXLR faders. Fails with an error when the GoXLR Utility is not connected.

        fader: The fader letter: A, B, C or D.
        channel: The channel to put on it, like \"Game\" or \"Music\".
        Gives back: true when done.
        Same as cb.call(\"goxlr.setFaderChannel\", ...).
        """
    def goxlr_set_fader_mute(self, target: Any, *, state: Any = None) -> Any:
        """Mutes or unmutes one GoXLR fader. Fails with an error when the GoXLR Utility is not connected or the channel is not on a fader.

        target: The fader letter A, B, C or D, or the channel on it, like \"Music\".
        state: Optional. \"Mute\", \"Mute to all\", \"Unmute\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new mute state: \"Unmuted\", \"MutedToX\" or \"MutedToAll\".
        Same as cb.call(\"goxlr.setFaderMute\", ...).
        """
    def goxlr_set_global_colour(self, colour: Any) -> Any:
        """Changes the colour of all the lights on your GoXLR. Fails with an error when the colour is not a hex colour.

        colour: The colour as a hex code, like \"#FF8800\".
        Gives back: true when done.
        Same as cb.call(\"goxlr.setGlobalColour\", ...).
        """
    def goxlr_set_routing(self, input: Any, output: Any, *, state: Any = None) -> Any:
        """Sends a GoXLR input to an output, or stops it, like the Routing table in the GoXLR Utility.

        input: The input: Microphone, Chat, Music, Game, Console, LineIn, System or Samples.
        output: The output: Headphones, BroadcastMix, ChatMic, Sampler, LineOut or StreamMix2.
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true when the input goes to the output, false when not.
        Same as cb.call(\"goxlr.setRouting\", ...).
        """
    def goxlr_set_sampler_bank(self, bank: Any) -> Any:
        """Switches the sampler to bank A, B or C. The GoXLR Mini has no sampler, so it fails with an error there.

        bank: The bank: \"A\", \"B\" or \"C\".
        Gives back: true when done.
        Same as cb.call(\"goxlr.setSamplerBank\", ...).
        """
    def goxlr_set_volume(self, channel: Any, volume: Any, *, relative: Any = None) -> Any:
        """Changes the volume of one GoXLR channel. Fails with an error when the GoXLR Utility is not connected.

        channel: The channel: Mic, LineIn, Console, System, Game, Chat, Sample, Music, Headphones, MicMonitor or LineOut.
        volume: The volume in percent, 0-100, like 80.
        relative: Optional. true adds the number to the current volume instead (use a minus number to turn it down). Default false.
        Gives back: The new volume in percent.
        Same as cb.call(\"goxlr.setVolume\", ...).
        """
    def goxlr_stop_sample(self, button: Any, *, bank: Any = None) -> Any:
        """Stops the sample that a sampler button is playing.

        button: The button: \"TopLeft\", \"TopRight\", \"BottomLeft\" or \"BottomRight\".
        bank: Optional. The bank: \"A\", \"B\" or \"C\". Leave it empty for the bank that is active now.
        Gives back: bank and button that were stopped.
        Same as cb.call(\"goxlr.stopSample\", ...).
        """
    def groups_add(self, group: Any, *, platform: Any = None, user: Any = None, user_id: Any = None) -> Any:
        """Puts a viewer in a group, like \"Giveaway\". The group is made when it does not exist yet. Leave the user out for the person who triggered the action.

        group: The group name, like \"Giveaway\".
        platform: Optional. The platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The user name, like \"CoolViewer42\" (a leading @ is fine). Leave it out for the user of the trigger.
        user_id: Optional. The user's id on that platform. Leave it out for the id of the trigger's user.
        Gives back: true when added, false when they were already in it.
        Same as cb.call(\"groups.add\", ...).
        """
    def groups_clear(self, group: Any) -> Any:
        """Takes every viewer out of a group. The group itself stays.

        group: The group name, like \"Giveaway\".
        Gives back: How many viewers were removed, or -1 when there is no such group.
        Same as cb.call(\"groups.clear\", ...).
        """
    def groups_contains(self, group: Any, *, platform: Any = None, user: Any = None, user_id: Any = None) -> Any:
        """Checks whether a viewer is in a group. Matches by user id when both have one, otherwise by name.

        group: The group name, like \"Giveaway\".
        platform: Optional. The platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The user name, like \"CoolViewer42\" (a leading @ is fine). Leave it out for the user of the trigger.
        user_id: Optional. The user's id on that platform. Leave it out for the id of the trigger's user.
        Gives back: true when they are in the group, otherwise false.
        Same as cb.call(\"groups.contains\", ...).
        """
    def groups_create(self, group: Any) -> Any:
        """Makes a new, empty group.

        group: The group name, like \"Giveaway\".
        Gives back: true when it was made, false when a group with that name already exists.
        Same as cb.call(\"groups.create\", ...).
        """
    def groups_delete(self, group: Any) -> Any:
        """Deletes a group and its member list.

        group: The group name, like \"Giveaway\".
        Gives back: true when it was deleted, false when there was no such group.
        Same as cb.call(\"groups.delete\", ...).
        """
    def groups_exists(self, group: Any) -> Any:
        """Checks whether a group with this name exists.

        group: The group name, like \"Giveaway\".
        Gives back: true when it exists, otherwise false.
        Same as cb.call(\"groups.exists\", ...).
        """
    def groups_list(self) -> Any:
        """Lists the names of all your groups.

        Gives back: A list of group names, like [\"Giveaway\", \"Regulars\"].
        Same as cb.call(\"groups.list\", ...).
        """
    def groups_remove(self, group: Any, *, platform: Any = None, user: Any = None, user_id: Any = None) -> Any:
        """Takes a viewer out of a group. Leave the user out for the person who triggered the action.

        group: The group name, like \"Giveaway\".
        platform: Optional. The platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The user name, like \"CoolViewer42\" (a leading @ is fine). Leave it out for the user of the trigger.
        user_id: Optional. The user's id on that platform. Leave it out for the id of the trigger's user.
        Gives back: true when removed, false when they were not in it.
        Same as cb.call(\"groups.remove\", ...).
        """
    def groups_users(self, group: Any) -> Any:
        """Lists the viewers in a group.

        group: The group name, like \"Giveaway\".
        Gives back: A list of members, each with platform, userId and userName. Empty when the group does not exist.
        Same as cb.call(\"groups.users\", ...).
        """
    def heartrate_get(self, *, source: Any = None) -> Any:
        """Gives you the newest heart rate reading from your heart rate monitor (through Pulsoid or HypeRate).

        source: Optional. Where from: \"Pulsoid\", \"HypeRate\" or \"Latest\" (the newest from either). Default \"Latest\".
        Gives back: bpm (beats per minute), source and time of the reading. null when no reading has come in yet.
        Same as cb.call(\"heartrate.get\", ...).
        """
    def hotkeys_list(self) -> Any:
        """Lists the hotkeys you made on the Hotkeys page.

        Gives back: A list of hotkeys. Each has id, name, gesture (the keys, like \"Ctrl+Shift+F1\"), enabled (true/false), action (the action it runs) and group.
        Same as cb.call(\"hotkeys.list\", ...).
        """
    def hotkeys_press(self, hotkey: Any) -> Any:
        """Acts as if you pressed one of your Cuebot hotkeys. Its \"Hotkey pressed\" trigger and its action run. No real key is pressed.

        hotkey: The hotkey's name as shown on the Hotkeys page, like \"Scene: BRB\", or its id.
        Gives back: true when the hotkey was found and pressed, false when no hotkey has that name or id.
        Same as cb.call(\"hotkeys.press\", ...).
        """
    def hyperate_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to HypeRate (heart rate) right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"hyperate.isConnected\", ...).
        """
    def input_key_press(self, keys: Any) -> Any:
        """Presses keys on your keyboard for you, in the app that is in front. On Linux your desktop asks once for permission. Fails with an error when the keys cannot be read or sent.

        keys: The keys to press, like \"Ctrl+Shift+F1\". Split several with commas, like \"Ctrl+C, Ctrl+V\". SendKeys text like \"^c\" or \"{ENTER}\" works too.
        Gives back: true when the keys were pressed.
        Same as cb.call(\"input.keyPress\", ...).
        """
    def input_status(self) -> Any:
        """Tells you how Cuebot presses keys and watches mouse clicks on this computer, and whether that works.

        Gives back: keyboard (the key pressing method), keyboardStatus, mouse (the mouse click method) and mouseStatus, all as text.
        Same as cb.call(\"input.status\", ...).
        """
    def kick_ban(self, user: Any, *, reason: Any = None) -> Any:
        """Bans a viewer from your Kick chat for good. Fails when you are not signed in to Kick or the user cannot be found.

        user: The Kick user name (a leading @ is fine) or their number id, like \"CoolViewer42\".
        reason: Optional. Why they were banned, like \"Spam\".
        Gives back: Nothing.
        Same as cb.call(\"kick.ban\", ...).
        """
    def kick_delete_message(self, message_id: Any) -> Any:
        """Deletes one message from your Kick chat. Fails when the message id is empty.

        message_id: The id of the chat message, like the %messageId% of a chat trigger.
        Gives back: Nothing.
        Same as cb.call(\"kick.deleteMessage\", ...).
        """
    def kick_get_broadcaster(self) -> Any:
        """Gives you the details of your Kick account and bot account as Cuebot knows them right now. Values are empty when you are not signed in.

        Gives back: An object with broadcastUser, broadcastUserId, broadcasterChannelSlug, broadcasterIsLive, broadcasterTitle, broadcasterCategory, broadcasterViewerCount, botUser and botUserId.
        Same as cb.call(\"kick.getBroadcaster\", ...).
        """
    def kick_get_user_info(self, user: Any) -> Any:
        """Looks up a Kick viewer's profile and channel. Fails when the user cannot be found.

        user: The Kick user name (a leading @ is fine) or their number id, like \"CoolViewer42\".
        Gives back: An object with user, userName, userId, profilePicture, channelSlug, isLive, streamTitle, category and description.
        Same as cb.call(\"kick.getUserInfo\", ...).
        """
    def kick_reply(self, message: Any, reply_to: Any, *, use_bot: Any = None, fallback: Any = None) -> Any:
        """Replies to a message in your Kick chat. Long text is split into several messages; only the first one is the reply.

        message: What to say, like \"Thanks for asking!\"
        reply_to: The id of the message to answer, like the %messageId% of a chat trigger.
        use_bot: Optional. true sends it with your bot account. Leave it out to send as yourself.
        fallback: Optional. With useBot, true (the default) sends as yourself when the bot is not signed in; false makes it fail instead.
        Gives back: The id of the message that was sent last, or null when the message was empty.
        Same as cb.call(\"kick.reply\", ...).
        """
    def kick_send_message(self, message: Any, *, use_bot: Any = None, fallback: Any = None) -> Any:
        """Sends a message in your Kick chat. Long text is split into several messages. Fails when you are not signed in to Kick.

        message: What to say, like \"Welcome to the stream!\"
        use_bot: Optional. true sends it with your bot account. Leave it out to send as yourself.
        fallback: Optional. With useBot, true (the default) sends as yourself when the bot is not signed in; false makes it fail instead.
        Gives back: The id of the message that was sent last, or null when the message was empty.
        Same as cb.call(\"kick.sendMessage\", ...).
        """
    def kick_set_category(self, category: Any) -> Any:
        """Changes the category of your Kick stream. A name picks the exact match, or else the first search result. Fails when nothing matches.

        category: The category name, like \"Just Chatting\", or its number id.
        Gives back: The name of the category that was set.
        Same as cb.call(\"kick.setCategory\", ...).
        """
    def kick_set_title(self, title: Any) -> Any:
        """Changes the title of your Kick stream. Fails when the title is empty.

        title: The new title, like \"Chill games with chat\".
        Gives back: Nothing.
        Same as cb.call(\"kick.setTitle\", ...).
        """
    def kick_timeout(self, user: Any, *, seconds: Any = None, reason: Any = None) -> Any:
        """Times out a viewer in your Kick chat. Kick counts in whole minutes, so seconds are rounded up (1 minute to 7 days).

        user: The Kick user name (a leading @ is fine) or their number id, like \"CoolViewer42\".
        seconds: Optional. How long, in seconds, like 300. Leave it out for 300 (5 minutes).
        reason: Optional. Why they were timed out, like \"Calm down\".
        Gives back: Nothing.
        Same as cb.call(\"kick.timeout\", ...).
        """
    def kick_unban(self, user: Any) -> Any:
        """Lets a banned or timed-out viewer chat on Kick again.

        user: The Kick user name (a leading @ is fine) or their number id, like \"CoolViewer42\".
        Gives back: Nothing.
        Same as cb.call(\"kick.unban\", ...).
        """
    def lumia_chatbot_message(self, message: Any, *, platform: Any = None, as_self: Any = None) -> Any:
        """Sends a chat message through Lumia Stream's chat bot. Fails with an error when Lumia Stream is not connected.

        message: What to say, like \"The lights are now purple!\" Needed.
        platform: Optional. Where to send it: \"twitch\", \"youtube\", \"facebook\", \"kick\", \"discord\" or \"all\". Default \"twitch\".
        as_self: Optional. true sends it as your own account instead of the bot. Default false.
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.chatbotMessage\", ...).
        """
    def lumia_connect(self) -> Any:
        """Starts connecting to Lumia Stream. It does not wait: Cuebot keeps trying in the background until it works.

        Gives back: true once connecting has started (not when it is connected).
        Same as cb.call(\"lumia.connect\", ...).
        """
    def lumia_disconnect(self) -> Any:
        """Disconnects Cuebot from Lumia Stream.

        Gives back: true when done.
        Same as cb.call(\"lumia.disconnect\", ...).
        """
    def lumia_get_info(self) -> Any:
        """Tells you what your Lumia Stream offers: its commands, alerts, lights and voices. Cuebot reads this when it connects.

        Gives back: commands (names by kind), alerts, lights (each with id, name, type), voices and states (on, streamMode, fuze; or null).
        Same as cb.call(\"lumia.getInfo\", ...).
        """
    def lumia_get_variable(self, name: Any) -> Any:
        """Reads one of your Lumia Stream variables. Fails with an error when Lumia Stream is not connected.

        name: The variable's name in Lumia Stream, like \"deaths\". Needed.
        Gives back: The variable's value as text (empty when Lumia sends nothing).
        Same as cb.call(\"lumia.getVariable\", ...).
        """
    def lumia_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Lumia Stream right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"lumia.isConnected\", ...).
        """
    def lumia_send(self, type: Any, *, params: Any = None) -> Any:
        """Sends any request to Lumia Stream's Send API. For advanced users who know Lumia's request types.

        type: The request type, like \"studio-scene\". Needed.
        params: Optional. The request's settings as an object, like {\"value\": \"Sunset\"}.
        Gives back: Lumia Stream's reply message, or null when it sends none.
        Same as cb.call(\"lumia.send\", ...).
        """
    def lumia_send_alert(self, alert: Any, *, extra_settings: Any = None) -> Any:
        """Fakes a Lumia Stream alert, like a follow, so its light show plays. Fails with an error when Lumia Stream is not connected.

        alert: The alert's name in Lumia Stream, like \"twitch-follower\". Needed.
        extra_settings: Optional. Extra values for the alert, like {\"username\": \"CoolViewer\"}.
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.sendAlert\", ...).
        """
    def lumia_send_command(self, command: Any, *, type: Any = None, hold: Any = None, skip_queue: Any = None, extra_settings: Any = None) -> Any:
        """Runs one of your Lumia Stream commands, like a light scene. Fails with an error when Lumia Stream is not connected or says no.

        command: The command's name as set in Lumia Stream, like \"party\". Needed.
        type: Optional. The kind of command: \"chat-command\", \"chatbot-command\", \"twitch-points\", \"twitch-extension\" or \"kick-points\". Default \"chat-command\".
        hold: Optional. true keeps the lights this way afterwards instead of going back. Default false.
        skip_queue: Optional. true runs it right away instead of waiting in Lumia's queue. Default false.
        extra_settings: Optional. Extra values for the command, like {\"username\": \"CoolViewer\"}.
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.sendCommand\", ...).
        """
    def lumia_set_color(self, color: Any, *, brightness: Any = None, transition: Any = None, duration: Any = None, lights: Any = None, hold: Any = None, skip_queue: Any = None) -> Any:
        """Changes the colour of your Lumia Stream lights. Fails with an error when Lumia Stream is not connected.

        color: The colour as \"#RRGGBB\", like \"#FF4076\", or \"r,g,b\", like \"255,64,118\". Needed.
        brightness: Optional. How bright, 0-100.
        transition: Optional. How long the colour fades in, in milliseconds.
        duration: Optional. How long the colour stays, in milliseconds.
        lights: Optional. Which lights, by name or id, split with commas, like \"Hue go, hue:19\". Leave it out for all lights.
        hold: Optional. true keeps this colour afterwards instead of going back. Default false.
        skip_queue: Optional. true does it right away instead of waiting in Lumia's queue. Default false.
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.setColor\", ...).
        """
    def lumia_set_to_default(self) -> Any:
        """Puts your Lumia Stream lights back to their default look.

        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.setToDefault\", ...).
        """
    def lumia_set_variable(self, name: Any, value: Any) -> Any:
        """Changes one of your Lumia Stream variables. Fails with an error when Lumia Stream is not connected.

        name: The variable's name in Lumia Stream, like \"deaths\". Needed.
        value: The new value: text, a number, true/false or an object. Counters also take \"+1\", \"-1\" or \"=5\".
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.setVariable\", ...).
        """
    def lumia_tts(self, message: Any, *, voice: Any = None, volume: Any = None) -> Any:
        """Says text out loud with Lumia Stream's text-to-speech. Fails with an error when Lumia Stream is not connected.

        message: What to say, like \"Welcome to the stream!\" Needed.
        voice: Optional. The Lumia voice name. Leave it out for Lumia's default voice.
        volume: Optional. How loud, 0-100.
        Gives back: true when Lumia Stream accepted it.
        Same as cb.call(\"lumia.tts\", ...).
        """
    def meldstudio_connect(self) -> Any:
        """Starts connecting to Meld Studio. It does not wait: Cuebot keeps trying in the background until it works.

        Gives back: true once connecting has started (not when it is connected).
        Same as cb.call(\"meldstudio.connect\", ...).
        """
    def meldstudio_disconnect(self) -> Any:
        """Disconnects Cuebot from Meld Studio.

        Gives back: true when done.
        Same as cb.call(\"meldstudio.disconnect\", ...).
        """
    def meldstudio_get_scenes(self) -> Any:
        """Lists your Meld Studio scenes in order.

        Gives back: A list of scenes. Each has id, name, current (true when showing) and staged (true when lined up next).
        Same as cb.call(\"meldstudio.getScenes\", ...).
        """
    def meldstudio_get_session(self) -> Any:
        """Gives you everything Meld Studio reports about your setup: scenes, layers, effects and audio tracks. For advanced scripts.

        Gives back: An object with one entry per item, named by its id. Each entry holds the item's details as Meld Studio sends them.
        Same as cb.call(\"meldstudio.getSession\", ...).
        """
    def meldstudio_get_status(self) -> Any:
        """Tells you whether Meld Studio is connected, recording or live, and which scene is showing.

        Gives back: isConnected, isRecording, isStreaming (true/false), currentScene (its name, or null) and apiVersion.
        Same as cb.call(\"meldstudio.getStatus\", ...).
        """
    def meldstudio_hide_layer(self, layer: Any, *, scene: Any = None) -> Any:
        """Hides a layer in Meld Studio. Fails with an error when no layer matches.

        layer: The layer's name, like \"Webcam\", or its id.
        scene: Optional. The scene the layer is in. Leave it out to search the current scene first, then all scenes.
        Gives back: false (the layer is hidden now).
        Same as cb.call(\"meldstudio.hideLayer\", ...).
        """
    def meldstudio_invoke(self, method: Any, *, args: Any = None) -> Any:
        """Calls any of Meld Studio's own functions by name. For advanced users. Fails with an error when this Meld Studio version does not have it.

        method: The Meld function name, like \"setStagedScene\". Needed.
        args: Optional. A list of values to pass to it, like [\"<scene id>\"].
        Gives back: Meld Studio's answer, or null when it gives none.
        Same as cb.call(\"meldstudio.invoke\", ...).
        """
    def meldstudio_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Meld Studio right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"meldstudio.isConnected\", ...).
        """
    def meldstudio_recording(self, *, state: Any = None) -> Any:
        """Starts or stops recording in Meld Studio. Fails with an error when Meld Studio is not connected.

        state: Optional. \"Start\", \"Stop\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: true when recording now, false when not.
        Same as cb.call(\"meldstudio.recording\", ...).
        """
    def meldstudio_screenshot(self, *, vertical: Any = None) -> Any:
        """Takes a screenshot in Meld Studio.

        vertical: Optional. true takes it of the vertical (phone) canvas. Default false.
        Gives back: true when done.
        Same as cb.call(\"meldstudio.screenshot\", ...).
        """
    def meldstudio_send_command(self, command: Any) -> Any:
        """Runs one of Meld Studio's own commands, like saving a clip or showing the replay.

        command: The Meld command name, like \"meld.recordClip\" or \"meld.replay.show\".
        Gives back: true when done.
        Same as cb.call(\"meldstudio.sendCommand\", ...).
        """
    def meldstudio_set_effect_enabled(self, layer: Any, effect: Any, *, state: Any = None, scene: Any = None) -> Any:
        """Turns an effect on a Meld Studio layer on or off, like a blur or colour filter. Fails with an error when the layer or effect is not found.

        layer: The layer's name, like \"Webcam\", or its id.
        effect: The effect's name, like \"Blur\", or its id.
        state: Optional. \"Enabled\", \"Disabled\" or \"Toggle\" (switch). Default \"Toggle\".
        scene: Optional. The scene the layer is in. Leave it out to search the current scene first.
        Gives back: true when the effect is on now, false when off.
        Same as cb.call(\"meldstudio.setEffectEnabled\", ...).
        """
    def meldstudio_set_layer_visibility(self, layer: Any, *, state: Any = None, scene: Any = None) -> Any:
        """Shows, hides or flips a layer in Meld Studio. Fails with an error when no layer matches.

        layer: The layer's name, like \"Webcam\", or its id.
        state: Optional. \"Visible\", \"Hidden\" or \"Toggle\" (switch). Default \"Toggle\".
        scene: Optional. The scene the layer is in. Leave it out to search the current scene first, then all scenes.
        Gives back: true when the layer is visible now, false when hidden.
        Same as cb.call(\"meldstudio.setLayerVisibility\", ...).
        """
    def meldstudio_set_track_gain(self, track: Any, gain: Any) -> Any:
        """Changes the volume (gain) of an audio track in Meld Studio. Fails with an error when no track matches.

        track: The audio track's name, like \"Music\", or its id.
        gain: The volume as a number from 0 (silent) to 1 (full), like 0.5. It must be a number, not text.
        Gives back: true when done.
        Same as cb.call(\"meldstudio.setTrackGain\", ...).
        """
    def meldstudio_set_track_muted(self, track: Any, *, state: Any = None) -> Any:
        """Mutes or unmutes an audio track in Meld Studio. Fails with an error when no track matches.

        track: The audio track's name, like \"Microphone\", or its id.
        state: Optional. \"Muted\", \"NotMuted\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: true when the track is muted now, false when not.
        Same as cb.call(\"meldstudio.setTrackMuted\", ...).
        """
    def meldstudio_show_layer(self, layer: Any, *, scene: Any = None) -> Any:
        """Makes a layer in Meld Studio visible. Fails with an error when no layer matches.

        layer: The layer's name, like \"Webcam\", or its id.
        scene: Optional. The scene the layer is in. Leave it out to search the current scene first, then all scenes.
        Gives back: true (the layer is visible now).
        Same as cb.call(\"meldstudio.showLayer\", ...).
        """
    def meldstudio_show_scene(self, scene: Any) -> Any:
        """Switches Meld Studio to another scene. Fails with an error when no scene matches or Meld Studio is not connected.

        scene: The scene's name as Meld Studio shows it, like \"Gameplay\", or its id.
        Gives back: The name of the scene that is now showing.
        Same as cb.call(\"meldstudio.showScene\", ...).
        """
    def meldstudio_show_scene_by_name(self, scene_name: Any) -> Any:
        """Switches Meld Studio to another scene. Works just like meldstudio.showScene.

        scene_name: The scene's name as Meld Studio shows it, like \"Gameplay\". An id works too.
        Gives back: The name of the scene that is now showing.
        Same as cb.call(\"meldstudio.showSceneByName\", ...).
        """
    def meldstudio_streaming(self, *, state: Any = None) -> Any:
        """Goes live or stops streaming in Meld Studio. Fails with an error when Meld Studio is not connected.

        state: Optional. \"Start\", \"Stop\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: true when streaming now, false when not.
        Same as cb.call(\"meldstudio.streaming\", ...).
        """
    def midi_close(self, device: Any, *, direction: Any = None) -> Any:
        """Closes one of your MIDI connections. Fails with an error when no connection has that name.

        device: The connection's name from the MIDI page, like \"Launchpad\". Empty means the first one.
        direction: Optional. \"in\" for a MIDI In connection, \"out\" for MIDI Out. Default \"out\".
        Gives back: true when done.
        Same as cb.call(\"midi.close\", ...).
        """
    def midi_devices(self) -> Any:
        """Lists the MIDI In and MIDI Out connections you set up on the MIDI page.

        Gives back: in and out: each a list of connections with id, name, device, state, open, present (plugged in), message and settings. MIDI In ones also list their events.
        Same as cb.call(\"midi.devices\", ...).
        """
    def midi_is_available(self) -> Any:
        """Checks whether MIDI works on this computer. Use midi.status to see why when it does not.

        Gives back: true when MIDI works here, otherwise false.
        Same as cb.call(\"midi.isAvailable\", ...).
        """
    def midi_open(self, device: Any, *, direction: Any = None) -> Any:
        """Opens one of your MIDI connections so it can send or receive. Fails with an error when no connection has that name.

        device: The connection's name from the MIDI page, like \"Launchpad\". Empty means the first one.
        direction: Optional. \"in\" for a MIDI In connection, \"out\" for MIDI Out. Default \"out\".
        Gives back: true when the connection is open now, false when it could not be opened.
        Same as cb.call(\"midi.open\", ...).
        """
    def midi_ports(self, *, direction: Any = None) -> Any:
        """Looks for MIDI ports on your computer right now, like a plugged-in keyboard or a virtual MIDI cable.

        direction: Optional. \"in\" for inputs, \"out\" for outputs. Leave it empty for both.
        Gives back: in and/or out: each a list of port names.
        Same as cb.call(\"midi.ports\", ...).
        """
    def midi_send_control_change(self, device: Any, *, channel: Any = None, controller: Any = None, value: Any = None) -> Any:
        """Sends a Control Change to a MIDI Out connection, like turning a knob or moving a slider.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        channel: Optional. The MIDI channel, 1-16. Empty or \"all\" sends on all 16. Default 1.
        controller: Optional. Which knob or slider (controller number), 0-127. Default 0.
        value: Optional. The new value, 0-127. Default 0.
        Gives back: true when sent.
        Same as cb.call(\"midi.sendControlChange\", ...).
        """
    def midi_send_event(self, device: Any, command: Any, *, channel: Any = None, data1: Any = None, data2: Any = None) -> Any:
        """Sends any channel message to a MIDI Out connection. Pick the kind of message and its two numbers.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        command: The kind of message: NoteOn, NoteOff, ControlChange, PatchChange, ChannelAfterTouch, PitchWheelChange or KeyAfterTouch.
        channel: Optional. The MIDI channel, 1-16. Empty or \"all\" sends on all 16. Default 1.
        data1: Optional. The first number, 0-127 (like the note or controller). For PitchWheelChange without data2: the whole value 0-16383. Default 0.
        data2: Optional. The second number, 0-127 (like velocity or value). PatchChange and ChannelAfterTouch ignore it.
        Gives back: true when sent.
        Same as cb.call(\"midi.sendEvent\", ...).
        """
    def midi_send_note_off(self, device: Any, *, channel: Any = None, note: Any = None, velocity: Any = None) -> Any:
        """Sends Note Off (letting go of a key) to a MIDI Out connection.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        channel: Optional. The MIDI channel, 1-16. Empty or \"all\" sends on all 16. Default 1.
        note: Optional. The note: a number 0-127 or a name like \"C4\". Default 60.
        velocity: Optional. How fast the key is let go, 0-127. Default 0.
        Gives back: true when sent.
        Same as cb.call(\"midi.sendNoteOff\", ...).
        """
    def midi_send_note_on(self, device: Any, *, channel: Any = None, note: Any = None, velocity: Any = None, duration: Any = None, note_off: Any = None) -> Any:
        """Sends a note (like pressing a piano key) to a MIDI Out connection. It opens the connection if needed. Can also let go of the note after a while.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        channel: Optional. The MIDI channel, 1-16. Empty or \"all\" sends on all 16. Default 1.
        note: Optional. The note: a number 0-127 (60 is middle C) or a name like \"C4\". Default 60.
        velocity: Optional. How hard the key is pressed, 0-127. Default 127.
        duration: Optional. With noteOff: how long to hold the note, in milliseconds, before letting go. Default 0.
        note_off: Optional. true also sends Note Off (lets go) after the duration. Your script waits for it. Default false.
        Gives back: true when sent.
        Same as cb.call(\"midi.sendNoteOn\", ...).
        """
    def midi_send_raw(self, device: Any, data: Any) -> Any:
        """Sends one complete MIDI message, written as hex bytes, to a MIDI Out connection. For advanced users.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        data: The message as hex bytes, like \"B0 07 64\" (Control Change 7 to 100 on channel 1).
        Gives back: true when sent.
        Same as cb.call(\"midi.sendRaw\", ...).
        """
    def midi_send_sysex(self, device: Any, data: Any) -> Any:
        """Sends a SysEx message (a device-specific message) to a MIDI Out connection. F0 at the start and F7 at the end are added when missing.

        device: The MIDI Out connection's name from the MIDI page. Empty means the first one.
        data: The message as hex bytes, like \"F0 7E 7F 06 01 F7\".
        Gives back: true when sent.
        Same as cb.call(\"midi.sendSysex\", ...).
        """
    def midi_status(self) -> Any:
        """Tells you how MIDI is working in Cuebot right now.

        Gives back: backend (the MIDI library in use), available (true/false), reason (why not, when unavailable), state and message (a status line).
        Same as cb.call(\"midi.status\", ...).
        """
    def mtion_get_active_clubhouse(self) -> Any:
        """Tells you which clubhouse is open in mtion studio right now.

        Gives back: id, name and description of the open clubhouse, or null when none is open (or mtion studio is closed).
        Same as cb.call(\"mtion.getActiveClubhouse\", ...).
        """
    def mtion_get_clubhouses(self) -> Any:
        """Lists every clubhouse of the mtion account that is signed in.

        Gives back: A list of clubhouses. Each has id, name and description.
        Same as cb.call(\"mtion.getClubhouses\", ...).
        """
    def mtion_get_triggers(self) -> Any:
        """Lists the external triggers in the clubhouse that is open in mtion studio.

        Gives back: A list of triggers. Each has id, name and parameters (each with index, name and type).
        Same as cb.call(\"mtion.getTriggers\", ...).
        """
    def mtion_is_running(self) -> Any:
        """Checks whether mtion studio answered Cuebot the last time Cuebot asked it something.

        Gives back: true when mtion studio answered last time, otherwise false.
        Same as cb.call(\"mtion.isRunning\", ...).
        """
    def mtion_send_trigger(self, trigger: Any, *, parameters: Any = None) -> Any:
        """Sets off an external trigger in your open mtion clubhouse, like confetti. It does not fail when mtion studio is closed; check the result instead.

        trigger: The trigger's name in your clubhouse, like \"Confetti\", or its id.
        parameters: Optional. Values for the trigger, by parameter name, like {\"Amount\": 5}. Lists of options take the option number.
        Gives back: running (mtion studio answered), found (the trigger exists) and sent (it was set off), each true or false.
        Same as cb.call(\"mtion.sendTrigger\", ...).
        """
    def nowplaying_control(self, *, command: Any = None, player: Any = None) -> Any:
        """Presses a media button for your music player, like play, pause or next song. Fails with an error when no music player is open.

        command: Optional. Which button: \"PlayPause\", \"Play\", \"Pause\", \"Next\" or \"Previous\". Default \"PlayPause\".
        player: Optional. Part of the player's name, like \"vlc\". Leave it out for the player that is playing now.
        Gives back: The name of the player that got the button press.
        Same as cb.call(\"nowplaying.control\", ...).
        """
    def nowplaying_get(self) -> Any:
        """Tells you which song is playing on your computer right now, from any music player (or Spotify when you are signed in).

        Gives back: found (false when no player is open), title, artist, album, player (its name), artUrl (cover picture link), duration in seconds, link and playing (true/false).
        Same as cb.call(\"nowplaying.get\", ...).
        """
    def obs_connections(self) -> Any:
        """Lists every OBS connection you set up in Cuebot.

        Gives back: A list with one entry per connection: name, state (Disconnected, Connecting, Connected or Error) and scene (its current scene).
        Same as cb.call(\"obs.connections\", ...).
        """
    def obs_get_current_scene(self, *, connection: Any = None) -> Any:
        """Asks OBS which scene is live right now. Fails with an error when OBS is not connected.

        connection: Optional. The name of the OBS connection to use. Leave it out for your main OBS.
        Gives back: The name of the live (program) scene, like \"Gameplay\".
        Same as cb.call(\"obs.getCurrentScene\", ...).
        """
    def obs_request(self, request_type: Any, *, request_data: Any = None, connection: Any = None) -> Any:
        """Sends any obs-websocket command to OBS and gives back OBS's answer. Good for things Cuebot has no ready-made step for. Fails with an error when OBS is not connected.

        request_type: The obs-websocket command name, like \"GetVersion\" or \"SetCurrentProgramScene\". Needed.
        request_data: Optional. The settings for the command, like {\"sceneName\": \"Gameplay\"}. Leave it out when the command needs none.
        connection: Optional. The name of the OBS connection to use. Leave it out for your main OBS.
        Gives back: OBS's answer data for that command (its responseData).
        Same as cb.call(\"obs.request\", ...).
        """
    def obs_request_batch(self, requests: Any, *, halt_on_failure: Any = None, connection: Any = None) -> Any:
        """Sends several obs-websocket commands to OBS in one go. One failed command does not stop the rest, unless you ask it to. Fails with an error when OBS is not connected.

        requests: A list of commands. Each one has a requestType and, if needed, requestData, like {\"requestType\": \"GetVersion\"}. Needed.
        halt_on_failure: Optional. true stops at the first command that fails. Default false.
        connection: Optional. The name of the OBS connection to use. Leave it out for your main OBS.
        Gives back: A list with one result per command: its requestType, requestStatus (did it work) and responseData.
        Same as cb.call(\"obs.requestBatch\", ...).
        """
    def obs_set_scene(self, scene_name: Any, *, connection: Any = None) -> Any:
        """Switches OBS to another scene. Fails with an error when OBS is not connected.

        scene_name: The scene name exactly as OBS shows it, like \"Gameplay\".
        connection: Optional. The name of the OBS connection to use. Leave it out for your main OBS.
        Gives back: Nothing.
        Same as cb.call(\"obs.setScene\", ...).
        """
    def obsbot_connect(self) -> Any:
        """Starts connecting to OBSBOT Center. Cuebot keeps trying in the background until it works.

        Gives back: true once connecting has started.
        Same as cb.call(\"obsbot.connect\", ...).
        """
    def obsbot_disconnect(self) -> Any:
        """Disconnects Cuebot from OBSBOT Center.

        Gives back: true when done.
        Same as cb.call(\"obsbot.disconnect\", ...).
        """
    def obsbot_get_state(self) -> Any:
        """Asks your OBSBOT camera for its current settings, like zoom and where it is pointing.

        Gives back: Values named obsbot.deviceIndex, obsbot.deviceName, obsbot.awake, obsbot.zoom, obsbot.fov, obsbot.pitch, obsbot.yaw, obsbot.aiLocked, obsbot.autoFraming, obsbot.deviceCount and obsbot.preset1.exists / .name (and 2, 3).
        Same as cb.call(\"obsbot.getState\", ...).
        """
    def obsbot_is_connected(self) -> Any:
        """Checks whether Cuebot can reach your OBSBOT camera (through OBSBOT Center) right now.

        Gives back: true while OBSBOT Center answers, otherwise false.
        Same as cb.call(\"obsbot.isConnected\", ...).
        """
    def obsbot_send(self, address: Any, *, args: Any = None) -> Any:
        """Sends any raw OSC message to OBSBOT Center. For advanced users who know OBSBOT's OSC addresses.

        address: The OSC address, like \"/OBSBOT/WebCam/General/SetZoom\". Needed.
        args: Optional. A list of values to send with it, like [50].
        Gives back: true when sent.
        Same as cb.call(\"obsbot.send\", ...).
        """
    def obsbot_set_gimbal(self, *, pan: Any = None, pitch: Any = None, speed: Any = None, device: Any = None) -> Any:
        """Points your OBSBOT camera in a direction. Numbers past the camera's limit are cut down to the limit.

        pan: Optional. Left/right in degrees, -129 to 129 (a Tail 2 reached directly: -159 to 159). Default 0 (straight ahead).
        pitch: Optional. Up/down in degrees, -59 to 59 (a Tail 2 reached directly: -62 to 62). Default 0.
        speed: Optional. How fast it turns, 0 to 90. Default 50.
        device: Optional. Which camera in OBSBOT Center, 1 to 4. Leave it out for the selected one.
        Gives back: true when sent.
        Same as cb.call(\"obsbot.setGimbal\", ...).
        """
    def obsbot_set_zoom(self, zoom: Any, *, device: Any = None) -> Any:
        """Zooms your OBSBOT camera in or out.

        zoom: How far to zoom, 0 (all the way out) to 100 (all the way in).
        device: Optional. Which camera in OBSBOT Center, 1 to 4. Leave it out for the selected one.
        Gives back: true when sent.
        Same as cb.call(\"obsbot.setZoom\", ...).
        """
    def obsbot_trigger_preset(self, *, preset: Any = None, device: Any = None) -> Any:
        """Moves your OBSBOT camera to one of its saved positions (presets).

        preset: Optional. Which preset, 1 to 3. Default 1.
        device: Optional. Which camera in OBSBOT Center, 1 to 4. Leave it out for the selected one.
        Gives back: true when sent.
        Same as cb.call(\"obsbot.triggerPreset\", ...).
        """
    def pally_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Pally.gg right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"pally.isConnected\", ...).
        """
    def pally_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a Pally.gg campaign tip inside Cuebot, so you can test your triggers. Nothing is sent to Pally.gg and it works without a connection.

        kind: Optional. Which event. Only \"campaignTip\" exists, and it is the default.
        Gives back: true when the test tip was sent, false when the kind is unknown.
        Same as cb.call(\"pally.sendTestEvent\", ...).
        """
    def points_add(self, amount: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Gives a viewer points. A negative amount takes points away. The balance never goes below 0. Leave the user out for the person who triggered the action.

        amount: How many points, a whole number like 100. Use a negative number to take points away.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The viewer's new balance.
        Same as cb.call(\"points.add\", ...).
        """
    def points_award_group(self, group: Any, amount: Any, *, apply_multipliers: Any = None, currency: Any = None) -> Any:
        """Gives points to every viewer in a user group (see the Groups page), like a bonus for regulars. Fails when the group does not exist.

        group: The group name, like \"Regulars\".
        amount: How many points, a whole number like 100.
        apply_multipliers: Optional. true multiplies each viewer's amount by their multiplier. Leave it out for false.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: How many viewers got points.
        Same as cb.call(\"points.awardGroup\", ...).
        """
    def points_currencies(self) -> Any:
        """Lists your points currencies, like \"points\" or \"coins\". The default currency comes first.

        Gives back: A list. Each currency has name, singularName, symbol, variableName and enabled.
        Same as cb.call(\"points.currencies\", ...).
        """
    def points_format(self, amount: Any, *, currency: Any = None) -> Any:
        """Turns a number into nice points text, like \"1,250 points\", \"1 point\" or \"🍪1,250\" when the currency has a symbol.

        amount: How many points, a whole number like 100.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The text, like \"1,250 points\".
        Same as cb.call(\"points.format\", ...).
        """
    def points_get(self, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Reads how many points a viewer has. Leave the user out for the person who triggered the action. Fails when the viewer cannot be found.

        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The balance, a whole number.
        Same as cb.call(\"points.get\", ...).
        """
    def points_leaderboard(self, *, platform: Any = None, count: Any = None, currency: Any = None) -> Any:
        """Gives you the viewers with the most points, highest first. Handy for a !top command.

        platform: Optional. Only viewers of this platform, like \"twitch\". Leave it out for every platform.
        count: Optional. How many to give back. Leave it out for 10; 0 gives everyone.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: A list. Each place has rank, platform, userId, userName and points.
        Same as cb.call(\"points.leaderboard\", ...).
        """
    def points_multiplier(self, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Tells you how much a viewer's automatic earnings are multiplied, from their role, their groups and their own multiplier.

        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The multiplier, like 1.5 (1 means normal).
        Same as cb.call(\"points.multiplier\", ...).
        """
    def points_remove(self, amount: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Takes points from a viewer. The balance never goes below 0. Leave the user out for the person who triggered the action.

        amount: How many points, a whole number like 100.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The viewer's new balance.
        Same as cb.call(\"points.remove\", ...).
        """
    def points_set(self, amount: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Sets a viewer's points to an exact number. Leave the user out for the person who triggered the action.

        amount: How many points, a whole number like 100.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: The new balance.
        Same as cb.call(\"points.set\", ...).
        """
    def points_set_group_multiplier(self, group: Any, multiplier: Any, *, currency: Any = None) -> Any:
        """Sets a multiplier for everyone in a user group, so they earn more (or less) points automatically.

        group: The group name, like \"Regulars\".
        multiplier: A number like 1.5 or \"2x\". Leave it empty (or \"none\") to remove the multiplier.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: true. Fails when the group is empty or the multiplier is not a number.
        Same as cb.call(\"points.setGroupMultiplier\", ...).
        """
    def points_set_user_multiplier(self, multiplier: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Sets one viewer's own multiplier, so they earn more (or less) points automatically. Leave the user out for the person who triggered the action.

        multiplier: A number like 1.5 or \"2x\". Leave it empty (or \"none\") to remove the multiplier.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: true. Fails when the viewer cannot be found or the multiplier is not a number.
        Same as cb.call(\"points.setUserMultiplier\", ...).
        """
    def points_spend(self, amount: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, currency: Any = None) -> Any:
        """Takes points from a viewer only when they have enough, like paying for a reward. Nothing changes when they are short.

        amount: How many points, a whole number like 100.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The viewer's name, like \"CoolViewer42\" (a leading @ is fine). They must have chatted or have points before.
        user_id: Optional. The viewer's id on that platform. Leave user and userId out for the person who triggered the action.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: true when the points were paid, false when the viewer did not have enough.
        Same as cb.call(\"points.spend\", ...).
        """
    def points_transfer(self, to: Any, amount: Any, *, platform: Any = None, user: Any = None, user_id: Any = None, to_user_id: Any = None, to_platform: Any = None, currency: Any = None) -> Any:
        """Moves points from one viewer to another, like a !give command. Fails when the giver has too few points or gives to themselves.

        to: The name of the viewer who gets the points, like \"CoolViewer42\". They must have chatted or have points before.
        amount: How many points to give, a whole number above 0, like 100.
        platform: Optional. The giver's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user: Optional. The giver's name. Leave user and userId out for the person who triggered the action.
        user_id: Optional. The giver's id on that platform.
        to_user_id: Optional. The receiver's id, instead of the name.
        to_platform: Optional. The receiver's platform. Leave it out for the giver's platform.
        currency: Optional. Which currency, by name, like \"coins\". Leave it out for your default currency.
        Gives back: An object with from (the giver's new balance) and to (the receiver's new balance).
        Same as cb.call(\"points.transfer\", ...).
        """
    def polypop_is_connected(self) -> Any:
        """Checks whether PolyPop is connected to Cuebot right now (at least one Cuebot plugin in PolyPop).

        Gives back: true while at least one PolyPop plugin is connected, otherwise false.
        Same as cb.call(\"polypop.isConnected\", ...).
        """
    def polypop_trigger_alert(self, alert_name: Any, *, variables: Any = None) -> Any:
        """Sets off a WebSocket Alert in PolyPop and hands it some words to show. Fails with an error when PolyPop is not connected.

        alert_name: The alert's name as set in PolyPop, like \"Follow\". Needed.
        variables: Optional. Words for the alert to use, like {\"user\": \"CoolViewer\"}. Every value is sent as text.
        Gives back: How many PolyPop plugins got the alert.
        Same as cb.call(\"polypop.triggerAlert\", ...).
        """
    def pronouns_get(self, *, user: Any = None) -> Any:
        """Looks up the pronouns a Twitch viewer set on pronouns.alejo.io. Leave the user out for the person who triggered the action.

        user: Optional. The viewer's Twitch login name, like \"coolviewer42\". Leave it out for the user of the trigger.
        Gives back: An object with display (like \"She/Her\"), subject, object, singular, altSubject and altObject. null when they set none or the site cannot be reached.
        Same as cb.call(\"pronouns.get\", ...).
        """
    def pulsoid_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Pulsoid (heart rate) right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"pulsoid.isConnected\", ...).
        """
    def quotes_add(self, text: Any, *, user: Any = None, user_id: Any = None, platform: Any = None, game: Any = None) -> Any:
        """Saves a new quote. It gets the next number. Leave the user out for the person who triggered the action. Fails when the text is empty.

        text: The quote, like \"I never miss a jump.\"
        user: Optional. Who said it, like \"CoolStreamer\". Leave it out for the user of the trigger.
        user_id: Optional. That person's user id. Leave it out for the id of the trigger's user.
        platform: Optional. The platform, like \"twitch\". Leave it out for the platform of the trigger.
        game: Optional. The game, like \"Celeste\". Leave it out for the game Cuebot last saw you stream.
        Gives back: The new quote, with id, text, user, userId, platform, game and createdAt.
        Same as cb.call(\"quotes.add\", ...).
        """
    def quotes_count(self) -> Any:
        """Counts your saved quotes.

        Gives back: The number of quotes.
        Same as cb.call(\"quotes.count\", ...).
        """
    def quotes_delete(self, id: Any) -> Any:
        """Deletes a quote.

        id: The quote number, like 12 (\"#12\" works too).
        Gives back: true when it existed, otherwise false.
        Same as cb.call(\"quotes.delete\", ...).
        """
    def quotes_edit(self, id: Any, *, text: Any = None, user: Any = None, game: Any = None) -> Any:
        """Changes a quote. Only what you pass is changed; the rest stays.

        id: The quote number, like 12 (\"#12\" works too).
        text: Optional. The new quote text.
        user: Optional. The new name of who said it.
        game: Optional. The new game.
        Gives back: The changed quote with id, text, user, userId, platform, game and createdAt, or null when there is no such quote.
        Same as cb.call(\"quotes.edit\", ...).
        """
    def quotes_get(self, id: Any) -> Any:
        """Gets one quote by its number.

        id: The quote number, like 12 (\"#12\" works too).
        Gives back: An object with id, text, user, userId, platform, game and createdAt. null when there is no such quote.
        Same as cb.call(\"quotes.get\", ...).
        """
    def quotes_random(self, *, search: Any = None, show: Any = None) -> Any:
        """Picks a random quote. Can pick only among quotes that contain a word, and can fire the Quote shown trigger.

        search: Optional. Only quotes whose text, user or game contains this, like \"jump\". A number also matches that quote id.
        show: Optional. true also runs your Quote shown trigger with the picked quote. Leave it out for false.
        Gives back: The quote with id, text, user, userId, platform, game and createdAt, or null when none match.
        Same as cb.call(\"quotes.random\", ...).
        """
    def quotes_search(self, query: Any) -> Any:
        """Finds quotes whose text, user or game contains a word. Upper or lower case does not matter.

        query: What to look for, like \"jump\". Leave it empty for every quote.
        Gives back: A list of quotes, each with id, text, user, userId, platform, game and createdAt.
        Same as cb.call(\"quotes.search\", ...).
        """
    def quotes_show(self, id: Any) -> Any:
        """Runs your Quote shown trigger for a quote, so an overlay or action can display it.

        id: The quote number, like 12 (\"#12\" works too).
        Gives back: The quote with id, text, user, userId, platform, game and createdAt, or null when there is no such quote.
        Same as cb.call(\"quotes.show\", ...).
        """
    def remote_get_instances(self) -> Any:
        """Lists the other Cuebot computers set up on the Remote page, and the ones connected to you, with their status.

        Gives back: A list. Each item has name, outgoing (true when you connect to it), state (like \"Connected\"), message, instanceId, instanceName, instanceOs, instanceVersion and connectedAt.
        Same as cb.call(\"remote.getInstances\", ...).
        """
    def remote_send_signal(self, name: Any, *, instance: Any = None, args: Any = None) -> Any:
        """Sends a named signal to another Cuebot computer, so an action there can react to it. Fails when the name is empty.

        name: The signal name, like \"sceneChanged\".
        instance: Optional. Which computer to send to (its name or id). Leave it out for every connected one.
        args: Optional. Extra values to send along, as an object, like { \"scene\": \"Gameplay\" }.
        Gives back: How many computers got the signal (0 when none are connected).
        Same as cb.call(\"remote.sendSignal\", ...).
        """
    def remote_wait_for_signal(self, name: Any, *, instance: Any = None, timeout_ms: Any = None) -> Any:
        """Waits until another Cuebot computer sends a signal with this name. Only signals that arrive after you start waiting count.

        name: The signal name to wait for, like \"ready\".
        instance: Optional. Only accept it from this computer (its name or id). Leave it out for any computer.
        timeout_ms: Optional. How long to wait, in milliseconds. Leave it out for 30000 (30 seconds); 0 waits forever.
        Gives back: The signal with name, sender (the action that sent it), instanceId, instanceName, instanceLabel, instanceOs, instanceVersion and args. null when time ran out.
        Same as cb.call(\"remote.waitForSignal\", ...).
        """
    def rumble_send_message(self, message: Any) -> Any:
        """Sends a message in your Rumble chat. Only works while you are live on Rumble and signed in; otherwise it fails. Empty text is skipped.

        message: What to say, like \"Welcome to the stream!\"
        Gives back: Nothing.
        Same as cb.call(\"rumble.sendMessage\", ...).
        """
    def slobs_call(self, resource: Any, method: Any, *, args: Any = None) -> Any:
        """Sends any Remote Control command to Streamlabs Desktop and gives back its answer. For advanced users. Fails with an error when Streamlabs Desktop is not connected.

        resource: The part of Streamlabs Desktop to talk to, like \"ScenesService\" or \"StreamingService\". Needed.
        method: The command name, like \"getScenes\" or \"toggleStreaming\". Needed.
        args: Optional. A list of values the command needs, like [\"Gameplay\"].
        Gives back: Streamlabs Desktop's answer for that command.
        Same as cb.call(\"slobs.call\", ...).
        """
    def slobs_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Streamlabs Desktop right now. Use it before slobs.call to skip the call when Streamlabs Desktop is closed.

        Gives back: true when it is connected, otherwise false.
        Same as cb.call(\"slobs.isConnected\", ...).
        """
    def speech_activate_voice_gate(self, profile: Any) -> Any:
        """Switches to a VoiceGate profile. VoiceGate holds speech back while you are talking. Use \"none\" (or empty) to switch VoiceGate off.

        profile: The profile name as set on the Speech page, like \"Talking\". \"none\" switches it off.
        Gives back: true when it worked, false when no profile has that name.
        Same as cb.call(\"speech.activateVoiceGate\", ...).
        """
    def speech_clear(self) -> Any:
        """Throws away every text that is waiting in the speech queue. The text being spoken right now keeps playing.

        Gives back: Nothing.
        Same as cb.call(\"speech.clear\", ...).
        """
    def speech_pause(self, *, paused: Any = None) -> Any:
        """Pauses the speech queue, or starts it again. Texts keep waiting while it is paused.

        paused: Optional. true pauses, false starts it again. Default true.
        Gives back: Nothing.
        Same as cb.call(\"speech.pause\", ...).
        """
    def speech_set_enabled(self, *, enabled: Any = None) -> Any:
        """Turns all text-to-speech on or off. While it is off, new texts are not spoken.

        enabled: Optional. true turns speech on, false turns it off. Default true.
        Gives back: Nothing.
        Same as cb.call(\"speech.setEnabled\", ...).
        """
    def speech_set_events(self, *, enabled: Any = None) -> Any:
        """Turns spoken event alerts (like follows and raids) on or off.

        enabled: Optional. true turns event speech on, false turns it off. Default true.
        Gives back: Nothing.
        Same as cb.call(\"speech.setEvents\", ...).
        """
    def speech_set_user_voice(self, platform: Any, alias: Any, *, user: Any = None, user_id: Any = None) -> Any:
        """Gives a viewer their own voice. Their chat messages are then read with it. An empty alias resets them to the normal voice.

        platform: The platform of the viewer, like \"twitch\".
        alias: The voice name (alias) from the Speech page, like \"Robot\". Empty resets the voice.
        user: Optional. The viewer's name, like \"CoolViewer\". Give this, the user id, or both.
        user_id: Optional. The viewer's user id. You can give this, the name, or both.
        Gives back: Nothing.
        Same as cb.call(\"speech.setUserVoice\", ...).
        """
    def speech_speak(self, text: Any, *, alias: Any = None, wait: Any = None, silent: Any = None, bad_word_filter: Any = None) -> Any:
        """Says text out loud with your text-to-speech voice. It joins the speech queue. Nothing is said when speech is turned off or the text is empty.

        text: What to say, like \"Welcome to the stream!\"
        alias: Optional. The voice name (alias) to use, as set on the Speech page. Leave it out for your default voice.
        wait: Optional. true waits until the text has been spoken before your script goes on. Default false.
        silent: Optional. true only makes the sound file and does not play it. Default false.
        bad_word_filter: Optional. true runs the text through your speech word filters first. Default false.
        Gives back: Nothing, unless wait or silent is true. Then it gives back success (false when skipped or speech is off), duration in milliseconds, and speechFile (the saved sound file, or empty).
        Same as cb.call(\"speech.speak\", ...).
        """
    def speech_state(self) -> Any:
        """Tells you what text-to-speech is doing right now.

        Gives back: enabled, paused, eventsEnabled (true/false), queueLength (how many texts wait), current (the text being spoken, or empty) and voiceGate (true while VoiceGate holds speech back).
        Same as cb.call(\"speech.state\", ...).
        """
    def speech_stop(self) -> Any:
        """Stops the text that is being spoken right now. The queue goes on with the next one.

        Gives back: Nothing.
        Same as cb.call(\"speech.stop\", ...).
        """
    def speech_stop_user(self, platform: Any, *, user: Any = None, user_id: Any = None) -> Any:
        """Throws away a viewer's waiting texts and stops theirs if it is playing right now.

        platform: The platform of the viewer, like \"twitch\".
        user: Optional. The viewer's name, like \"SpammyViewer\". Give this, the user id, or both.
        user_id: Optional. The viewer's user id. You can give this, the name, or both.
        Gives back: How many texts were stopped or thrown away (0 when no name or id was given).
        Same as cb.call(\"speech.stopUser\", ...).
        """
    def spending_add(self, amount: Any, *, user: Any = None, platform: Any = None, currency: Any = None, label: Any = None) -> Any:
        """Adds a purchase by hand, like a cash gift. It counts toward the top spenders like any other purchase.

        amount: How much, like 10.
        user: Optional. Who spent the money. Leave it out for the user of the trigger.
        platform: Optional. The platform to list them under. Leave it out for the platform of the trigger (or Manual).
        currency: Optional. The currency, like \"EUR\". Leave it out for your chosen currency.
        label: Optional. What it was, like \"Cash gift\".
        Gives back: true. Fails when the user is empty or the amount is not more than 0.
        Same as cb.call(\"spending.add\", ...).
        """
    def spending_new_stream(self) -> Any:
        """Starts a new stream for the top spenders: \"stream\" lists start empty and exchange rates are fetched again. Cuebot does this by itself when you go live.

        Gives back: true.
        Same as cb.call(\"spending.newStream\", ...).
        """
    def spending_platforms(self) -> Any:
        """Lists the platforms anyone has spent money on, so you can ask for each platform's top spenders.

        Gives back: A list of platform names, like [\"Twitch\", \"YouTube\"].
        Same as cb.call(\"spending.platforms\", ...).
        """
    def spending_remove(self, user: Any, *, platform: Any = None, period: Any = None) -> Any:
        """Removes what one person spent, for example after a refund or a test.

        user: Whose purchases to remove.
        platform: Optional. Only on this platform. Leave it out for every platform.
        period: Optional. stream, day, week, month, year or all. Leave it out for all.
        Gives back: How many purchases were removed.
        Same as cb.call(\"spending.remove\", ...).
        """
    def spending_stream(self) -> Any:
        """Tells you about the current stream: when it started and which exchange rates it locked in.

        Gives back: An object with id, startedAt, endedAt, currency, ratesSource and ratesDate, or null before anything was bought.
        Same as cb.call(\"spending.stream\", ...).
        """
    def spending_top(self, *, period: Any = None, platform: Any = None, count: Any = None) -> Any:
        """Gives you the top spenders of a period, on one platform or all of them, highest first, in your chosen currency.

        period: Optional. stream, day, week, month, year or all. Leave it out for stream.
        platform: Optional. One platform, like \"Twitch\". Leave it out for every platform together.
        count: Optional. How many people, like 5. Leave it out for 10.
        Gives back: An object with period, platform, currency, total, pending (purchases still waiting for exchange rates) and spenders. Each spender has rank, user, platform, total, count and breakdown (what they spent on each kind of thing, like Cheer or Super Chat).
        Same as cb.call(\"spending.top\", ...).
        """
    def spotify_add_to_queue(self, song: Any) -> Any:
        """Adds a song to your Spotify queue. Great for song requests. Fails with an error when no song is found or Spotify says no (for example, nothing is playing).

        song: A song name to search for, like \"Never Gonna Give You Up\", or a Spotify song link (open.spotify.com/track/...).
        Gives back: The song that was added: title, artist, uri (Spotify's id for it) and link.
        Same as cb.call(\"spotify.addToQueue\", ...).
        """
    def spotify_is_signed_in(self) -> Any:
        """Checks whether Cuebot is signed in to your Spotify account.

        Gives back: true when signed in, otherwise false.
        Same as cb.call(\"spotify.isSignedIn\", ...).
        """
    def steps_check(self, left: Any, operator: Any, right: Any) -> Any:
        """Checks a condition the same way the If step does: the same comparisons, numbers compared as numbers, and capital letters do not matter. %variables% in both values are filled in first.

        left: The value to look at, like \"%user%\".
        operator: How to compare, like \"equals\", \"contains\", \"greater than\", \"matches regex\", \"exists\" or \"does not exist\".
        right: What to compare with, like \"5\".
        Gives back: true when the condition holds, otherwise false.
        Same as cb.call(\"steps.check\", ...).
        """
    def steps_pick(self, weights: Any) -> Any:
        """Picks one choice at random, where a bigger weight means a bigger chance, like a Group set to \"Run one random step\".

        weights: One weight per choice, like [1, 3]: the second is three times as likely. 0 is never picked (unless all are 0).
        Gives back: The position of the picked choice: 0 for the first, 1 for the second, and so on. -1 when the list is empty.
        Same as cb.call(\"steps.pick\", ...).
        """
    def steps_run(self, type: Any, parameters: Any) -> Any:
        """Runs one step, exactly like it would run in the action editor. Its settings can use %variables% and ~globals~; they are filled in first. Code made with \"Export to code\" uses this for every step. Steps that hold other steps (Group, Switch, While, Run at the same time) need steps.runList instead.

        type: The step's id, like \"core.log\" or \"obs.setScene\". The Sub-actions reference shows the id of every step.
        parameters: The step's settings by name, like {\"message\": \"Hi %user%\"}. Settings you leave out use their usual default.
        Gives back: An object: outcome is \"completed\", or \"stop\" when the step stopped the action; branch is the path a step with paths picked (like \"True\"), or null.
        Same as cb.call(\"steps.run\", ...).
        """
    def steps_run_list(self, steps: Any) -> Any:
        """Runs a list of steps in order, with the steps inside them (the True/False paths of an If, the lanes of Run at the same time, and so on), exactly like the action editor would.

        steps: The steps, written the way actions.json stores them: each one has a type, its parameters and, for steps that hold others, branches (like {\"Lane 1\": [ ... ]}).
        Gives back: How the list ended: \"completed\", \"stop\" (a step stopped the action), \"break\" or \"continue\" (a Loop break or Loop continue step ran).
        Same as cb.call(\"steps.runList\", ...).
        """
    def streamdeck_get_instances(self) -> Any:
        """Lists the Stream Deck apps that are connected to Cuebot, with their devices and Cuebot keys.

        Gives back: A list of apps. Each has id, remote, pluginVersion, streamDeckVersion, platform, connectedAt, devices (name, type, size) and buttons (buttonId, row, column, state and more).
        Same as cb.call(\"streamdeck.getInstances\", ...).
        """
    def streamdeck_is_connected(self) -> Any:
        """Checks whether the Cuebot plugin in the Stream Deck app is connected right now.

        Gives back: true while at least one Stream Deck plugin is connected, otherwise false.
        Same as cb.call(\"streamdeck.isConnected\", ...).
        """
    def streamdeck_set_background(self, button_id: Any, image: Any, *, state: Any = None) -> Any:
        """Changes the picture on a Stream Deck key. Leave the image empty to put the normal picture back.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin. Needed.
        image: A picture file on your computer, a web link (http or https) or a data: link. Empty puts the normal picture back.
        state: Optional. Which state of a two-state key to change: 0 or 1. Leave it out for the state it shows now.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.setBackground\", ...).
        """
    def streamdeck_set_state(self, button_id: Any, *, state: Any = None) -> Any:
        """Switches a two-state Stream Deck key to state 0 or 1.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin. Needed.
        state: Optional. 0 or 1. Default 0.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.setState\", ...).
        """
    def streamdeck_set_title(self, button_id: Any, title: Any, *, state: Any = None) -> Any:
        """Changes the text shown on a Stream Deck key.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin, like \"deaths\". Needed.
        title: The text to show, like \"Deaths: 3\".
        state: Optional. Which state of a two-state key to change: 0 or 1. Leave it out for the state it shows now.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.setTitle\", ...).
        """
    def streamdeck_set_value(self, button_id: Any, value: Any, *, indicator: Any = None) -> Any:
        """Changes the value shown on a Stream Deck key or dial (Stream Deck +).

        button_id: The button id you gave the key or dial in the Cuebot Stream Deck plugin. Needed.
        value: The value to show, like \"75%\".
        indicator: Optional. For dials: how full the bar is, 0-100.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.setValue\", ...).
        """
    def streamdeck_show_alert(self, button_id: Any) -> Any:
        """Flashes the warning sign on a Stream Deck key for a moment.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin. Needed.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.showAlert\", ...).
        """
    def streamdeck_show_ok(self, button_id: Any) -> Any:
        """Flashes the check mark on a Stream Deck key for a moment.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin. Needed.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.showOk\", ...).
        """
    def streamdeck_toggle_state(self, button_id: Any) -> Any:
        """Flips a two-state Stream Deck key to its other state.

        button_id: The button id you gave the key in the Cuebot Stream Deck plugin. Needed.
        Gives back: How many keys have this button id (0 when none was found).
        Same as cb.call(\"streamdeck.toggleState\", ...).
        """
    def streamelements_add_user_points(self, user: Any, amount: Any) -> Any:
        """Gives a viewer StreamElements loyalty points. Use a minus number to take points away. Needs your StreamElements token; fails with an error otherwise.

        user: The viewer's name, like \"CoolViewer\". Needed.
        amount: How many points to add, like 100. Use a minus number, like -50, to take points away.
        Gives back: The viewer's new points total.
        Same as cb.call(\"streamelements.addUserPoints\", ...).
        """
    def streamelements_get_points_leaderboard(self, *, count: Any = None) -> Any:
        """Gets the viewers with the most StreamElements loyalty points. Needs your StreamElements token; fails with an error otherwise.

        count: Optional. How many people to get. Default 5.
        Gives back: A list of the viewers with the most points. Each has user and points.
        Same as cb.call(\"streamelements.getPointsLeaderboard\", ...).
        """
    def streamelements_get_tip_leaderboard(self, *, count: Any = None) -> Any:
        """Gets the viewers who tipped you the most on StreamElements. Needs your StreamElements token; fails with an error otherwise.

        count: Optional. How many people to get. Default 5.
        Gives back: A list of the top tippers. Each has user and total (how much they tipped).
        Same as cb.call(\"streamelements.getTipLeaderboard\", ...).
        """
    def streamelements_get_tips(self, *, count: Any = None) -> Any:
        """Gets your latest StreamElements tips. Needs your StreamElements token; fails with an error otherwise.

        count: Optional. How many tips to get. Default 5.
        Gives back: A list of tips. Each has user, amount, currency, formattedAmount (like \"$5.00\"), message and createdAt.
        Same as cb.call(\"streamelements.getTips\", ...).
        """
    def streamelements_get_user_points(self, user: Any) -> Any:
        """Looks up how many StreamElements loyalty points a viewer has. Needs your StreamElements token on the Donations page; fails with an error otherwise.

        user: The viewer's name, like \"CoolViewer\". A leading @ is fine. Needed.
        Gives back: user, points (now), pointsAllTime and rank.
        Same as cb.call(\"streamelements.getUserPoints\", ...).
        """
    def streamelements_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to StreamElements right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"streamelements.isConnected\", ...).
        """
    def streamelements_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a StreamElements event inside Cuebot, so you can test your triggers. Nothing is sent to StreamElements and it works without a connection.

        kind: Optional. Which event: \"tip\", \"merch\" or \"redemption\". Default \"tip\".
        Gives back: true when the test event was sent, false when the kind is unknown.
        Same as cb.call(\"streamelements.sendTestEvent\", ...).
        """
    def streamlabs_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Streamlabs right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"streamlabs.isConnected\", ...).
        """
    def streamlabs_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a Streamlabs event inside Cuebot, so you can test your triggers. Nothing is sent to Streamlabs and it works without a connection.

        kind: Optional. Which event: \"donation\", \"charity\", \"merch\" or \"loyalty\". Default \"donation\".
        Gives back: true when the test event was sent, false when the kind is unknown.
        Same as cb.call(\"streamlabs.sendTestEvent\", ...).
        """
    def streamloots_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to your StreamLoots alerts right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"streamloots.isConnected\", ...).
        """
    def streamloots_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a StreamLoots alert inside Cuebot, so you can test your triggers. Nothing is sent to StreamLoots and it works without a connection.

        kind: Optional. Which alert: \"purchase\", \"gift\", \"communityGift\" or \"redemption\" (a card was used). Default \"redemption\".
        Gives back: true when the test alert was sent, false when the kind is unknown.
        Same as cb.call(\"streamloots.sendTestEvent\", ...).
        """
    def subathon_add_time(self, seconds: Any, *, timer: Any = None, use_multiplier: Any = None) -> Any:
        """Adds seconds to a subathon timer, or takes them away with a negative number.

        seconds: How many seconds, like 300.
        timer: Optional. The timer's name. Leave it out for the first timer.
        use_multiplier: Optional. true to use the timer's multiplier.
        Gives back: The seconds really added (less when the longest length was reached).
        Same as cb.call(\"subathon.addTime\", ...).
        """
    def subathon_get(self, *, timer: Any = None) -> Any:
        """Gives you a subathon timer: its time left, state and settings.

        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: An object with id, name, state (Stopped, Running, Paused or Ended), remainingSeconds, elapsedSeconds, addedSeconds, multiplier, initialSeconds, maxSeconds and values, or null when there is no such timer.
        Same as cb.call(\"subathon.get\", ...).
        """
    def subathon_list(self) -> Any:
        """Gives you every subathon timer.

        Gives back: A list of timers, each like subathon.get gives.
        Same as cb.call(\"subathon.list\", ...).
        """
    def subathon_pause(self, *, timer: Any = None) -> Any:
        """Pauses a running subathon timer.

        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true.
        Same as cb.call(\"subathon.pause\", ...).
        """
    def subathon_reset(self, *, timer: Any = None) -> Any:
        """Stops a subathon timer and puts back its starting length.

        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true.
        Same as cb.call(\"subathon.reset\", ...).
        """
    def subathon_resume(self, *, timer: Any = None) -> Any:
        """Carries on with a paused subathon timer.

        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true.
        Same as cb.call(\"subathon.resume\", ...).
        """
    def subathon_set_multiplier(self, multiplier: Any, *, timer: Any = None) -> Any:
        """Changes how much event time counts, like 2 for happy hour. Time already added does not change.

        multiplier: 2 = double, 1 = normal, 0.5 = half.
        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true.
        Same as cb.call(\"subathon.setMultiplier\", ...).
        """
    def subathon_set_time(self, seconds: Any, *, timer: Any = None) -> Any:
        """Sets how many seconds a subathon timer has left.

        seconds: The time left in seconds.
        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true.
        Same as cb.call(\"subathon.setTime\", ...).
        """
    def subathon_start(self, *, timer: Any = None) -> Any:
        """Starts a subathon timer, or carries on after a pause.

        timer: Optional. The timer's name. Leave it out for the first timer.
        Gives back: true. Fails when there is no such timer.
        Same as cb.call(\"subathon.start\", ...).
        """
    def text_resolve(self, text: Any) -> Any:
        """Fills in the variables in a text, just like a sub-action does: %variables%, ~globals~ and $functions()$.

        text: The text with placeholders, like \"Thanks %user%, you are follower number ~followers~!\".
        Gives back: The text with every placeholder filled in.
        Same as cb.call(\"text.resolve\", ...).
        """
    def tipeeestream_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to TipeeeStream right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"tipeeestream.isConnected\", ...).
        """
    def tipeeestream_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a TipeeeStream event inside Cuebot, so you can test your triggers. Nothing is sent to TipeeeStream and it works without a connection.

        kind: Optional. Which event: \"donation\", \"follow\" or \"subscription\". Default \"donation\".
        Gives back: true when the test event was sent, false when the kind is unknown.
        Same as cb.call(\"tipeeestream.sendTestEvent\", ...).
        """
    def tits_activate_trigger(self, trigger: Any) -> Any:
        """Runs one of the triggers you set up in T.I.T.S., by its id or name. Fails with an error when T.I.T.S. is not connected.

        trigger: The trigger's id or name, like \"Rain\".
        Gives back: true when T.I.T.S. accepted it.
        Same as cb.call(\"tits.activateTrigger\", ...).
        """
    def tits_activate_trigger_by_name(self, trigger_name: Any) -> Any:
        """Runs one of the triggers you set up in T.I.T.S., by its name. Fails with an error when T.I.T.S. is not connected.

        trigger_name: The trigger's name as T.I.T.S. shows it, like \"Rain\".
        Gives back: true when T.I.T.S. accepted it.
        Same as cb.call(\"tits.activateTriggerByName\", ...).
        """
    def tits_connect(self) -> Any:
        """Starts connecting to T.I.T.S. It does not wait: Cuebot keeps trying in the background until it works.

        Gives back: true once connecting has started (not when it is connected).
        Same as cb.call(\"tits.connect\", ...).
        """
    def tits_disconnect(self) -> Any:
        """Disconnects Cuebot from T.I.T.S.

        Gives back: true when done.
        Same as cb.call(\"tits.disconnect\", ...).
        """
    def tits_get_items(self) -> Any:
        """Lists the items you can throw in T.I.T.S.

        Gives back: A list of items. Each has id and name.
        Same as cb.call(\"tits.getItems\", ...).
        """
    def tits_get_triggers(self) -> Any:
        """Lists the triggers you set up in T.I.T.S.

        Gives back: A list of triggers. Each has id and name.
        Same as cb.call(\"tits.getTriggers\", ...).
        """
    def tits_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to T.I.T.S. (Twitch Integrated Throwing System) right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"tits.isConnected\", ...).
        """
    def tits_throw_item_by_name(self, item_name: Any, *, delay: Any = None, amount: Any = None) -> Any:
        """Throws an item at your model in T.I.T.S. Fails with an error when T.I.T.S. has no item with that name or is not connected.

        item_name: The item's name as T.I.T.S. shows it, like \"Tomato\". An id works too.
        delay: Optional. Seconds between throws when throwing more than one. Default 0.05.
        amount: Optional. How many to throw, 1 or more. Default 1.
        Gives back: true when something was thrown.
        Same as cb.call(\"tits.throwItemByName\", ...).
        """
    def tits_throw_items(self, items: Any, *, delay: Any = None, amount: Any = None) -> Any:
        """Throws items at your model in T.I.T.S., picked at random from your list. Fails with an error when one of the items is unknown.

        items: The items to pick from, by name or id: a list like [\"Tomato\", \"Pillow\"], or text split with commas.
        delay: Optional. Seconds between throws. Default 0.05.
        amount: Optional. How many to throw, 1 or more. Default 1.
        Gives back: How many items were thrown.
        Same as cb.call(\"tits.throwItems\", ...).
        """
    def transfer_export(self, *, actions: Any = None, commands: Any = None, timers: Any = None, include_dependencies: Any = None, name: Any = None, author: Any = None, description: Any = None) -> Any:
        """Packs actions, commands and timers into one import text (starting with CUEBOT1:) that you can share. Fails when a name is not found.

        actions: Optional. Action names, separated by commas, like \"Shoutout, Raid alert\".
        commands: Optional. Command names, separated by commas, like \"!so, !lurk\".
        timers: Optional. Timer names, separated by commas.
        include_dependencies: Optional. true (the default) also packs what they need, like actions they run. false packs only what you named.
        name: Optional. A name for the pack, shown when someone imports it.
        author: Optional. Your name, shown when someone imports it.
        description: Optional. A short note about the pack, shown when someone imports it.
        Gives back: The import text, starting with CUEBOT1:.
        Same as cb.call(\"transfer.export\", ...).
        """
    def treatstream_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to TreatStream right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"treatstream.isConnected\", ...).
        """
    def treatstream_send_test_event(self, *, kind: Any = None) -> Any:
        """Fakes a TreatStream treat inside Cuebot, so you can test your triggers. Nothing is sent to TreatStream and it works without a connection.

        kind: Optional. Which event. Only \"treat\" exists, and it is the default.
        Gives back: true when the test treat was sent, false when the kind is unknown.
        Same as cb.call(\"treatstream.sendTestEvent\", ...).
        """
    def twitch_add_blocked_term(self, term: Any) -> Any:
        """Adds a word or phrase to your Twitch blocked terms, so messages with it are hidden. Fails when the term is empty. Fails when Twitch is not connected.

        term: The word or phrase to block, like \"buy followers\".
        Gives back: An object with blockedTermId.
        Same as cb.call(\"twitch.addBlockedTerm\", ...).
        """
    def twitch_add_moderator(self, *, user: Any = None) -> Any:
        """Makes a viewer a moderator in your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.addModerator\", ...).
        """
    def twitch_add_schedule_segment(self, *, start: Any = None, duration: Any = None, title: Any = None, category: Any = None, recurring: Any = None) -> Any:
        """Adds a stream to your Twitch schedule. Fails when Twitch is not connected.

        start: Optional. When it starts, like 2026-12-01 19:00, in this computer's time zone.
        duration: Optional. How long it runs, in minutes (30 to 1380).
        title: Optional. The stream's title, up to 140 characters.
        category: Optional. A category name or id. Leave empty for none.
        recurring: Optional. Tick to repeat it every week.
        Gives back: An object with scheduleSegmentId.
        Same as cb.call(\"twitch.addScheduleSegment\", ...).
        """
    def twitch_add_suspicious_status(self, *, user: Any = None, status: Any = None) -> Any:
        """Marks a viewer as suspicious. \"Active Monitoring\" flags their messages for your mods; \"Restricted\" keeps their messages out of chat. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        status: Optional. \"Active Monitoring\" (the default) or \"Restricted\".
        Gives back: An object with suspiciousUser, suspiciousUserName, suspiciousUserId, suspiciousUserType, suspiciousStatusResult, suspiciousStatus (like \"RESTRICTED\") and suspiciousTypes.
        Same as cb.call(\"twitch.addSuspiciousStatus\", ...).
        """
    def twitch_add_tag(self, tag: Any) -> Any:
        """Adds one tag to your Twitch channel. Your other tags stay. Fails when Twitch is not connected.

        tag: The tag, like \"Cozy\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.addTag\", ...).
        """
    def twitch_add_vip(self, *, user: Any = None) -> Any:
        """Makes a viewer a VIP in your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.addVip\", ...).
        """
    def twitch_assign_guest_star_slot(self, *, user: Any = None, slot: Any = None, session_id: Any = None) -> Any:
        """Puts an invited guest into a Twitch Guest Star slot. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        slot: Optional. The slot number, from 1 up to the number of slots, like \"2\". Leave it out for 1.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.assignGuestStarSlot\", ...).
        """
    def twitch_ban(self, *, user: Any = None, reason: Any = None) -> Any:
        """Bans a viewer from your Twitch chat for good. Fails when the user is not found. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        reason: Optional. Why, like \"Spam\".
        Gives back: An object with bannedUser (display name), bannedUserName (login), bannedUserId, bannedUserType (\"twitch\") and banResult (true).
        Same as cb.call(\"twitch.ban\", ...).
        """
    def twitch_block_user(self, *, user: Any = None) -> Any:
        """Blocks someone for your Twitch account, so they cannot whisper you or appear in your chat. Needs the Block users permission on the Twitch Settings tab. Fails when Twitch is not connected.

        user: Optional. Login name (a leading @ is fine) or id:12345. Leave empty for the person who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.blockUser\", ...).
        """
    def twitch_cancel_prediction(self) -> Any:
        """Cancels the running Twitch prediction and gives everyone their points back. Fails when Twitch is not connected.

        Gives back: An object with cancelled (true), active and the prediction's details: prediction.Id, prediction.Title, prediction.outcome0.title and so on.
        Same as cb.call(\"twitch.cancelPrediction\", ...).
        """
    def twitch_cancel_raid(self) -> Any:
        """Stops the raid you started, before it goes. Fails when Twitch is not connected.

        Gives back: Nothing.
        Same as cb.call(\"twitch.cancelRaid\", ...).
        """
    def twitch_cancel_schedule_segment(self, *, segment_id: Any = None, canceled: Any = None) -> Any:
        """Cancels one scheduled stream (it stays on the schedule, marked cancelled), or brings it back. Fails when Twitch is not connected.

        segment_id: Optional. The scheduled stream's id, from Get stream schedule or Add to stream schedule.
        canceled: Optional. Tick to cancel it; untick to bring it back.
        Gives back: Nothing.
        Same as cb.call(\"twitch.cancelScheduleSegment\", ...).
        """
    def twitch_clear_chat(self) -> Any:
        """Clears every message from your Twitch chat. Fails when Twitch is not connected.

        Gives back: An object with clearSuccess (true).
        Same as cb.call(\"twitch.clearChat\", ...).
        """
    def twitch_clear_tags(self) -> Any:
        """Removes every tag from your Twitch channel. Fails when Twitch is not connected.

        Gives back: Nothing.
        Same as cb.call(\"twitch.clearTags\", ...).
        """
    def twitch_configure_rewards(self, *, enable: Any = None, disable: Any = None, pause: Any = None, unpause: Any = None, disable_not_configured: Any = None) -> Any:
        """Turns many channel point rewards on, off, paused or unpaused in one go. Fails when a name matches no reward or group. Fails when Twitch is not connected.

        enable: Optional. Reward titles, ids or reward group names, one per line or separated by |.
        disable: Optional. Reward titles, ids or reward group names, one per line or separated by |.
        pause: Optional. Reward titles, ids or reward group names, one per line or separated by |.
        unpause: Optional. Reward titles, ids or reward group names, one per line or separated by |.
        disable_not_configured: Optional. true also turns off every reward that is in no reward group. Leave it out for false.
        Gives back: An object with rewardsChanged and rewardsSkipped (rewards Cuebot could not change).
        Same as cb.call(\"twitch.configureRewards\", ...).
        """
    def twitch_create_clip(self, *, has_delay: Any = None, title: Any = None, duration: Any = None) -> Any:
        """Makes a clip of your live Twitch stream. Fails when Twitch is not connected.

        has_delay: Optional. true makes the clip account for your stream delay. Leave it out for false.
        title: Optional. The clip's title. Leave it out to use your stream title.
        duration: Optional. The clip's length in seconds, 5 to 60. Leave it out for Twitch's 30 seconds.
        Gives back: An object with clipId, clipEditUrl, clipUrl, createClipSuccess, createClipId, createClipCreatedAt and createClipUrl.
        Same as cb.call(\"twitch.createClip\", ...).
        """
    def twitch_create_clip_from_vod(self, *, title: Any = None, seconds_ago: Any = None, duration: Any = None, video_id: Any = None, end_offset: Any = None) -> Any:
        """Clips a moment that already happened, from your current or a past broadcast. Use it for \"clip the last 2 minutes\" style commands. Twitch only allows this when past broadcasts are stored on your channel. Fails when Twitch is not connected.

        title: Optional. The clip's title. Twitch requires one.
        seconds_ago: Optional. When no video id is given: how many seconds ago the clip should end, counted back from now in the current broadcast. 0 ends it now.
        duration: Optional. How long the clip is, in seconds, from 5 to 60.
        video_id: Optional. The video to clip. Leave empty for your current (or latest) broadcast.
        end_offset: Optional. With a video id: where the clip ends, in seconds from the start of the video.
        Gives back: An object with clipId, clipUrl, clipEditUrl and clipVideoId.
        Same as cb.call(\"twitch.createClipFromVod\", ...).
        """
    def twitch_create_guest_star_session(self) -> Any:
        """Starts a Twitch Guest Star session. Fails when Twitch is not connected.

        Gives back: An object with guestStarActive, guestStarSessionId, guestStarGuestCount, and guestStarGuest0, guestStarGuest0UserName, guestStarGuest0UserId, guestStarGuest0Slot, guestStarGuest0IsLive, guestStarGuest0Volume and so on.
        Same as cb.call(\"twitch.createGuestStarSession\", ...).
        """
    def twitch_create_marker(self, *, description: Any = None) -> Any:
        """Adds a stream marker at this moment, so you can find it again later in your video. Fails when Twitch is not connected.

        description: Optional. A short note for the marker, like \"Epic win\".
        Gives back: An object with markerId, markerPosition (seconds into the stream), streamMarkerSuccess, streamMarkerId, streamMarkerCreatedAt, streamMarkerDescription and streamMarkerPosition.
        Same as cb.call(\"twitch.createMarker\", ...).
        """
    def twitch_create_poll(self, title: Any, choices: Any, *, duration: Any = None, points_per_vote: Any = None) -> Any:
        """Starts a Twitch poll in your chat with 2 to 5 choices. Fails when Twitch is not connected.

        title: The question, like \"Which game next?\"
        choices: The answers as text, one per line or separated by |, like \"Minecraft|Fortnite|Tetris\".
        duration: Optional. How long the poll runs, in seconds (15 to 1800). Leave it out for 60.
        points_per_vote: Optional. Channel points for each extra vote. Leave it out (or 0) to turn extra votes off.
        Gives back: An object with pollId and success (true).
        Same as cb.call(\"twitch.createPoll\", ...).
        """
    def twitch_create_prediction(self, title: Any, outcomes: Any, *, window: Any = None) -> Any:
        """Starts a Twitch prediction: viewers bet channel points on 2 to 10 outcomes. Fails when Twitch is not connected.

        title: The question, like \"Will I beat the boss?\"
        outcomes: The outcomes as text, one per line or separated by |, like \"Yes|No\".
        window: Optional. How long viewers can bet, in seconds (30 to 1800). Leave it out for 120.
        Gives back: An object with predictionId, prediction.Id and success (true).
        Same as cb.call(\"twitch.createPrediction\", ...).
        """
    def twitch_create_reward(self, title: Any, *, cost: Any = None, prompt: Any = None, input_required: Any = None, enabled: Any = None) -> Any:
        """Makes a new channel point reward. Cuebot can change it later because it made it. Fails when Twitch is not connected.

        title: The reward name, like \"Hydrate!\"
        cost: Optional. The price in channel points. Leave it out for 100.
        prompt: Optional. The text viewers see under the title.
        input_required: Optional. true makes viewers type a message when they redeem. Leave it out for false.
        enabled: Optional. false makes it hidden at first. Leave it out for true.
        Gives back: An object with createdRewardId.
        Same as cb.call(\"twitch.createReward\", ...).
        """
    def twitch_delete_guest_star_invite(self, *, user: Any = None, session_id: Any = None) -> Any:
        """Takes back a viewer's Twitch Guest Star invite. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.deleteGuestStarInvite\", ...).
        """
    def twitch_delete_guest_star_slot(self, *, user: Any = None, slot: Any = None, reinvite: Any = None, session_id: Any = None) -> Any:
        """Removes a guest from their Twitch Guest Star slot. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        slot: Optional. The slot number, from 1 up to the number of slots, like \"2\". Leave it out for 1.
        reinvite: Optional. true sends them back to the invite list. Leave it out for false.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.deleteGuestStarSlot\", ...).
        """
    def twitch_delete_message(self, *, message_id: Any = None) -> Any:
        """Deletes one message from your Twitch chat. Fails when Twitch is not connected.

        message_id: Optional. The id of the message. Leave it out for the chat message that triggered the action (%messageId%).
        Gives back: Nothing.
        Same as cb.call(\"twitch.deleteMessage\", ...).
        """
    def twitch_delete_reward(self, *, reward: Any = None) -> Any:
        """Deletes a channel point reward for good. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: Nothing.
        Same as cb.call(\"twitch.deleteReward\", ...).
        """
    def twitch_delete_schedule_segment(self, *, segment_id: Any = None) -> Any:
        """Removes a stream from your schedule. For a weekly stream this removes every week. Fails when Twitch is not connected.

        segment_id: Optional. The scheduled stream's id, from Get stream schedule or Add to stream schedule.
        Gives back: Nothing.
        Same as cb.call(\"twitch.deleteScheduleSegment\", ...).
        """
    def twitch_emote_only(self, *, enabled: Any = None) -> Any:
        """Turns emote-only chat on or off: viewers can only send emotes. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.emoteOnly\", ...).
        """
    def twitch_end_guest_star_session(self, *, session_id: Any = None) -> Any:
        """Ends a Twitch Guest Star session. Fails when Twitch is not connected.

        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.endGuestStarSession\", ...).
        """
    def twitch_end_poll(self, *, mode: Any = None) -> Any:
        """Ends the running Twitch poll early. Fails when Twitch is not connected.

        mode: Optional. \"Terminate\" (the default) ends it and shows the results; \"Archive\" ends it and hides them. Capital letters do not matter.
        Gives back: Nothing.
        Same as cb.call(\"twitch.endPoll\", ...).
        """
    def twitch_follower_mode(self, *, enabled: Any = None, minutes: Any = None) -> Any:
        """Turns follower-only chat on or off: only followers can chat, optionally after following for some minutes. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        minutes: Optional. How long someone must have followed, in minutes (0 to 129600, which is 3 months). Leave it out for 0.
        Gives back: Nothing.
        Same as cb.call(\"twitch.followerMode\", ...).
        """
    def twitch_get_ad_schedule(self) -> Any:
        """Gets your Twitch ad schedule: when the next ad comes and how many snoozes you have. Fails when Twitch is not connected.

        Gives back: An object with nextAdAt, lastAdAt, adDuration, prerollFreeTime, snoozeCount, snoozeRefreshAt, prerollFree and prerollFreeSeconds.
        Same as cb.call(\"twitch.getAdSchedule\", ...).
        """
    def twitch_get_automod_settings(self) -> Any:
        """Reads how strict AutoMod is in your chat, from 0 (off) to 4 (strictest) for each kind of message. Fails when Twitch is not connected.

        Gives back: An object with automodLevel, automodAggression, automodBullying, automodDisability, automodMisogyny, automodRaceEthnicityOrReligion, automodSexBasedTerms, automodSexualitySexOrGender and automodSwearing.
        Same as cb.call(\"twitch.getAutomodSettings\", ...).
        """
    def twitch_get_ban_status(self, *, user: Any = None) -> Any:
        """Checks whether someone is banned or timed out in your chat. Fails when Twitch is not connected.

        user: Optional. Login name (a leading @ is fine) or id:12345. Leave empty for the person who triggered the action.
        Gives back: An object with isBanned, isTimedOut, banReason, bannedAt, timeoutEndsAt and bannedBy.
        Same as cb.call(\"twitch.getBanStatus\", ...).
        """
    def twitch_get_bits_leaderboard(self, *, count: Any = None, period: Any = None) -> Any:
        """Gets your top cheerers for a day, week, month, year or all time. Fails when Twitch is not connected.

        count: Optional. How many people to get (1 to 100).
        period: Optional. The time span: all time, or the current day, week, month or year.
        Gives back: An object with leaderCount, leader0.user, leader0.userName, leader0.userId, leader0.score and leader0.rank.
        Same as cb.call(\"twitch.getBitsLeaderboard\", ...).
        """
    def twitch_get_blocked_terms(self) -> Any:
        """Lists every blocked word or phrase of your Twitch channel. Fails when Twitch is not connected.

        Gives back: A list, each with id and text.
        Same as cb.call(\"twitch.getBlockedTerms\", ...).
        """
    def twitch_get_bot(self) -> Any:
        """Gives you your Twitch bot account, as set on the Twitch page. Works without asking Twitch.

        Gives back: An object with userId, userName and connected. null when no bot account is signed in.
        Same as cb.call(\"twitch.getBot\", ...).
        """
    def twitch_get_broadcaster(self) -> Any:
        """Gives you your signed-in Twitch account as Cuebot knows it. Works without asking Twitch.

        Gives back: An object with userId, userName, connected (true when Twitch is connected) and scopes (the permissions you gave).
        Same as cb.call(\"twitch.getBroadcaster\", ...).
        """
    def twitch_get_broadcaster_info(self) -> Any:
        """Gets details about your own Twitch account, like partner or affiliate status. Fails when Twitch is not connected.

        Gives back: An object with broadcasterUserId, broadcasterUserName, broadcasterUser, broadcasterType, broadcasterIsAffiliate, broadcasterIsPartner, broadcasterProfileImageUrl, broadcasterDescription, botUserId, botUserName and more.
        Same as cb.call(\"twitch.getBroadcasterInfo\", ...).
        """
    def twitch_get_channel_info(self, *, user: Any = None) -> Any:
        """Gets a Twitch channel's details: title, category, tags and more. Fails when the channel is not found. Fails when Twitch is not connected.

        user: Optional. A channel's login name or \"id:12345\". Leave it out for your own channel.
        Gives back: An object with channelId, channelUser, channelUserName, channelTitle, channelGame, channelGameId, channelTags, channelLanguage, channelContentLabels and channelIsBrandedContent.
        Same as cb.call(\"twitch.getChannelInfo\", ...).
        """
    def twitch_get_charity_campaign(self) -> Any:
        """Gets your running Twitch charity campaign. Fails when Twitch is not connected.

        Gives back: An object with charityActive, charityName, charityCurrent, charityTarget and charityCurrency. Values are empty when no campaign runs.
        Same as cb.call(\"twitch.getCharityCampaign\", ...).
        """
    def twitch_get_charity_donations(self, *, count: Any = None) -> Any:
        """Lists the donations to your running charity campaign. Fails when Twitch is not connected.

        count: Optional. How many donations to get (1 to 100).
        Gives back: An object with donationCount, donation0.user, donation0.userName, donation0.amount and donation0.currency.
        Same as cb.call(\"twitch.getCharityDonations\", ...).
        """
    def twitch_get_chat_colors(self, users: Any) -> Any:
        """Gets the name colors people use in Twitch chat. Fails when Twitch is not connected.

        users: Login names or \"id:12345\", separated by commas (up to 100).
        Gives back: An object from user id to color, like #FF7F50. The color is empty for someone who never picked one.
        Same as cb.call(\"twitch.getChatColors\", ...).
        """
    def twitch_get_cheermotes(self) -> Any:
        """Lists the Bits cheer emotes (cheermotes) that work in your channel, exactly as Twitch gives them. Fails when Twitch is not connected.

        Gives back: A list of cheermotes in Twitch's own format (each with prefix, tiers and image links).
        Same as cb.call(\"twitch.getCheermotes\", ...).
        """
    def twitch_get_clip_download_urls(self, *, clip_id: Any = None) -> Any:
        """Gets download links for clips of your channel. Twitch's links only work for a short time. Fails when Twitch is not connected.

        clip_id: Optional. The clip id, or up to 10 ids separated by commas. Leave it out for %clipId% (for example from twitch.createClip).
        Gives back: An object with clipDownloadCount, clipDownloadUrl, clipLandscapeDownloadUrl and clipPortraitDownloadUrl (of the first clip), plus clipDownload0.clipId, clipDownload0.landscapeUrl and so on.
        Same as cb.call(\"twitch.getClipDownloadUrls\", ...).
        """
    def twitch_get_clips(self, *, user: Any = None, game_id: Any = None, started_at: Any = None, ended_at: Any = None, count: Any = None, featured: Any = None) -> Any:
        """Lists clips of a Twitch channel or of a category, most viewed first. Handy for a clip player. Fails when Twitch is not connected.

        user: Optional. A channel's login name or \"id:12345\". Leave it (and gameId) out for your own channel.
        game_id: Optional. A category id, to get that category's clips instead of a channel's.
        started_at: Optional. Only clips made after this date and time, like \"2026-10-01T00:00:00Z\".
        ended_at: Optional. Only clips made before this date and time.
        count: Optional. How many clips at most. Leave it out (or 0) for up to 1000.
        featured: Optional. true gives only featured clips, false only the others. Leave it out for all.
        Gives back: A list of clips, each with id, url, embedUrl, broadcasterName, creatorName, title, viewCount, createdAt, thumbnailUrl, duration, isFeatured and more.
        Same as cb.call(\"twitch.getClips\", ...).
        """
    def twitch_get_content_labels(self) -> Any:
        """Lists Twitch's content classification labels, such as Gambling or Mature-rated game, with their ids. Use an id with Set content label. Fails when Twitch is not connected.

        Gives back: A list of labels, each with id, name and description.
        Same as cb.call(\"twitch.getContentLabels\", ...).
        """
    def twitch_get_creator_goals(self) -> Any:
        """Lists the creator goals you have running on Twitch, like a follower or sub goal. Fails when Twitch is not connected.

        Gives back: An object with goalCount, goal0.type, goal0.description, goal0.current, goal0.target and goal0.id.
        Same as cb.call(\"twitch.getCreatorGoals\", ...).
        """
    def twitch_get_custom_power_ups(self) -> Any:
        """Lists your channel's custom Power-ups (the ones viewers buy with Bits). Fails when Twitch is not connected.

        Gives back: An object with powerUpCount, powerUp0.title, powerUp0.bits, powerUp0.enabled, powerUp0.paused, powerUp0.id and powerUp0.image.
        Same as cb.call(\"twitch.getCustomPowerUps\", ...).
        """
    def twitch_get_emotes(self, *, source: Any = None, set_ids: Any = None) -> Any:
        """Lists Twitch emotes with their images: your channel's own, Twitch's global ones, or the emotes of emote sets. Handy for emote walls. Fails when Twitch is not connected.

        source: Optional. \"channel\" (your emotes, the default), \"global\" or \"set\".
        set_ids: Optional. With source \"set\": emote set ids separated by commas (up to 25).
        Gives back: A list of emotes, each with id, name, type, tier, setId, isAnimated and imageUrl.
        Same as cb.call(\"twitch.getEmotes\", ...).
        """
    def twitch_get_follow_age(self, *, user: Any = None) -> Any:
        """Tells you if and how long a viewer has followed your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with isFollowing, followedAt, followAgeDays, followAge (like \"2 years, 3 months\"), followDate, followAgeLong, followAgeShort, followAge.years and more.
        Same as cb.call(\"twitch.getFollowAge\", ...).
        """
    def twitch_get_follower_count(self) -> Any:
        """Gets how many followers your Twitch channel has, and who followed last. Fails when Twitch is not connected.

        Gives back: An object with followerCount, latestFollower, latestFollowerUserName and latestFollowerUserId.
        Same as cb.call(\"twitch.getFollowerCount\", ...).
        """
    def twitch_get_guest_star_invites(self, *, session_id: Any = None) -> Any:
        """Lists the people invited to your Twitch Guest Star session. Fails when Twitch is not connected.

        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: An object with guestStarInviteCount, guestStarInvites (user ids, separated by commas), and guestStarInvite0UserId, guestStarInvite0Status and so on.
        Same as cb.call(\"twitch.getGuestStarInvites\", ...).
        """
    def twitch_get_guest_star_session(self) -> Any:
        """Gets your running Twitch Guest Star session and its guests. Fails when Twitch is not connected.

        Gives back: An object with guestStarActive, guestStarSessionId, guestStarGuestCount, and guestStarGuest0, guestStarGuest0UserName, guestStarGuest0UserId, guestStarGuest0Slot, guestStarGuest0IsLive, guestStarGuest0Volume and so on.
        Same as cb.call(\"twitch.getGuestStarSession\", ...).
        """
    def twitch_get_guest_star_settings(self) -> Any:
        """Gets your Twitch Guest Star settings. Fails when Twitch is not connected.

        Gives back: An object with guestStarSlotCount, guestStarLayout, guestStarModeratorSendLive, guestStarBrowserSourceAudio and guestStarBrowserSourceToken.
        Same as cb.call(\"twitch.getGuestStarSettings\", ...).
        """
    def twitch_get_hype_train(self) -> Any:
        """Checks whether a Hype Train is running right now and how far it is, plus your channel's record. Fails when Twitch is not connected.

        Gives back: An object with hypeTrainActive, hypeTrainLevel, hypeTrainProgress, hypeTrainGoal, hypeTrainPercent, hypeTrainTotal, hypeTrainType, hypeTrainExpiresAt, hypeTrainIsShared, hypeTrainTopContributor, hypeTrainTopContribution, hypeTrainRecordLevel, hypeTrainRecordTotal, hypeTrainRecordAt and hypeTrainId.
        Same as cb.call(\"twitch.getHypeTrain\", ...).
        """
    def twitch_get_latest_subscriber(self) -> Any:
        """Tells you who subscribed last, as Cuebot saw it.

        Gives back: An object with latestSubscriber.userName, latestSubscriber.userLogin, latestSubscriber.userId, latestSubscriber.tier (like \"Tier1\"), latestSubscriber.isGift and latestSubscriber.at.
        Same as cb.call(\"twitch.getLatestSubscriber\", ...).
        """
    def twitch_get_moderators(self) -> Any:
        """Lists every moderator of your Twitch channel. Fails when Twitch is not connected.

        Gives back: A list, each with userId, userLogin and userName.
        Same as cb.call(\"twitch.getModerators\", ...).
        """
    def twitch_get_pinned_message(self) -> Any:
        """Gets the message pinned in your Twitch chat right now. Fails when Twitch is not connected.

        Gives back: An object with isPinned, pinnedMessage, pinnedMessageId, pinnedUser, pinnedUserName, pinnedUserId, pinnedBy, pinnedAt and pinnedUntil. Text values are empty when nothing is pinned.
        Same as cb.call(\"twitch.getPinnedMessage\", ...).
        """
    def twitch_get_poll(self) -> Any:
        """Gets your latest Twitch poll and its votes. Fails when Twitch is not connected.

        Gives back: An object with pollId, pollTitle, pollStatus, pollIsActive, pollChoiceCount, pollChoice0, pollChoice0Votes and so on, active, plus poll.Title, poll.votes and more.
        Same as cb.call(\"twitch.getPoll\", ...).
        """
    def twitch_get_prediction(self) -> Any:
        """Gets your latest Twitch prediction and its bets. Fails when Twitch is not connected.

        Gives back: An object with predictionId, predictionTitle, predictionStatus, predictionOutcomeCount, predictionOutcome0, predictionOutcome0Users, predictionOutcome0Points and so on, active, plus prediction.* details.
        Same as cb.call(\"twitch.getPrediction\", ...).
        """
    def twitch_get_present_user(self, *, index: Any = None) -> Any:
        """Gets one person in your Twitch chat by their number in the list. Handy with a random number. Fails when Twitch is not connected.

        index: Optional. The number, starting at 0 for the first person. Leave it out for 0.
        Gives back: An object with presentUsersCount, presentUser (their name, or false when the number is too big), presentUserId, presentUserName and presentUserType.
        Same as cb.call(\"twitch.getPresentUser\", ...).
        """
    def twitch_get_present_viewers(self) -> Any:
        """Gets the people in your Twitch chat right now. Fails when Twitch is not connected.

        Gives back: An object with presentViewerCount and presentViewers (their login names, separated by commas).
        Same as cb.call(\"twitch.getPresentViewers\", ...).
        """
    def twitch_get_random_users(self, *, count: Any = None, exclude_broadcaster: Any = None) -> Any:
        """Picks random people from your Twitch chat. Handy for giveaways. Fails when Twitch is not connected.

        count: Optional. How many to pick. Leave it out for 1.
        exclude_broadcaster: Optional. true (the default) leaves you and your bot out; false lets them be picked.
        Gives back: An object with randomUserCount, and randomUser0, randomUserName0, randomUserId0, randomUserType0 for the first pick, and so on.
        Same as cb.call(\"twitch.getRandomUsers\", ...).
        """
    def twitch_get_reward_counter(self, *, reward: Any = None, user: Any = None) -> Any:
        """Tells you how often a channel point reward was redeemed, in total and by one viewer. Cuebot counts these itself. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        user: Optional. The viewer to count: a login name or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with counter (all redeems) and userCounter (that viewer's redeems).
        Same as cb.call(\"twitch.getRewardCounter\", ...).
        """
    def twitch_get_reward_info(self, *, reward: Any = None) -> Any:
        """Gets the settings of one channel point reward. Fails when no reward matches. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: An object with rewardId, rewardTitle, rewardPrompt, rewardCost, rewardEnabled, rewardPaused, rewardInStock, rewardBackgroundColor, rewardCooldown, rewardMaxPerStream, rewardMaxPerUser, rewardIsManageable and more.
        Same as cb.call(\"twitch.getRewardInfo\", ...).
        """
    def twitch_get_rewards(self) -> Any:
        """Lists every channel point reward of your Twitch channel. Fails when Twitch is not connected.

        Gives back: A list of rewards, each with id, title, prompt, cost, backgroundColor, isEnabled, isPaused, isInStock, maxPerStream, globalCooldownSeconds, isManageable (true when Cuebot can change it) and more.
        Same as cb.call(\"twitch.getRewards\", ...).
        """
    def twitch_get_reward_user_counters(self, reward: Any) -> Any:
        """Tells you how often each viewer redeemed a channel point reward, as Cuebot counted it. Fails when no reward matches. Fails when Twitch is not connected.

        reward: The reward's title or id, like \"Hydrate!\".
        Gives back: An object with each viewer's user id and their count, like { \"12345\": 3 }.
        Same as cb.call(\"twitch.getRewardUserCounters\", ...).
        """
    def twitch_get_schedule(self, *, count: Any = None) -> Any:
        """Reads your Twitch stream schedule, for example to answer a !schedule command. Fails when Twitch is not connected.

        count: Optional. How many upcoming streams to get (1 to 25).
        Gives back: An object with nextStreamAt, nextStreamAtLocal, nextStreamTitle, nextStreamCategory, scheduleCount, schedule0.title, onVacation, vacationStart and vacationEnd.
        Same as cb.call(\"twitch.getSchedule\", ...).
        """
    def twitch_get_shared_chat_session(self) -> Any:
        """Checks whether your chat is shared with other channels right now (Shared Chat). Fails when Twitch is not connected.

        Gives back: An object with sharedChatActive, sharedChatIsHost, sharedChatParticipantCount, sharedChatParticipantIds, sharedChatHostId and sharedChatSessionId.
        Same as cb.call(\"twitch.getSharedChatSession\", ...).
        """
    def twitch_get_shield_mode(self) -> Any:
        """Checks whether Twitch Shield Mode is on. Fails when Twitch is not connected.

        Gives back: An object with shieldModeActive and shieldModeModerator (who turned it on).
        Same as cb.call(\"twitch.getShieldMode\", ...).
        """
    def twitch_get_stream_info(self, *, user: Any = None) -> Any:
        """Checks whether a Twitch channel is live, with its viewers, title and uptime. Fails when Twitch is not connected.

        user: Optional. A channel's login name or \"id:12345\". Leave it out for your own channel.
        Gives back: An object with isLive, viewerCount, streamTitle, streamGame, streamStartedAt and uptimeSeconds. Values are empty or 0 when the channel is offline.
        Same as cb.call(\"twitch.getStreamInfo\", ...).
        """
    def twitch_get_stream_markers(self, *, count: Any = None) -> Any:
        """Lists the stream markers of your current (or latest) broadcast. Fails when Twitch is not connected.

        count: Optional. How many markers to get (1 to 100).
        Gives back: An object with markerCount, marker0.description, marker0.position, marker0.url and marker0.id.
        Same as cb.call(\"twitch.getStreamMarkers\", ...).
        """
    def twitch_get_sub_counter(self) -> Any:
        """Reads Cuebot's sub counter, for a sub goal or sub train.

        Gives back: An object with subCounter.count, subCounter.rollover and subCounter.rolloverCount.
        Same as cb.call(\"twitch.getSubCounter\", ...).
        """
    def twitch_get_subscriber_count(self) -> Any:
        """Gets how many subscribers your Twitch channel has. Fails when Twitch is not connected.

        Gives back: An object with subscriberCount and subPoints.
        Same as cb.call(\"twitch.getSubscriberCount\", ...).
        """
    def twitch_get_sub_status(self, *, user: Any = None) -> Any:
        """Checks whether a viewer subscribes to your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with isSubscribed, subTier, subIsGift and subGifter.
        Same as cb.call(\"twitch.getSubStatus\", ...).
        """
    def twitch_get_team_info(self, *, user: Any = None) -> Any:
        """Gets the Twitch teams a channel belongs to. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with teamCount, teams (names, separated by commas) and teamName (the first team).
        Same as cb.call(\"twitch.getTeamInfo\", ...).
        """
    def twitch_get_unban_requests(self, *, status: Any = None) -> Any:
        """Lists unban requests: people who were banned and asked to be let back in. Fails when Twitch is not connected.

        status: Optional. Which requests: pending (not answered yet), approved, denied, acknowledged or canceled.
        Gives back: An object with unbanRequestCount, unbanRequest0.id, unbanRequest0.user, unbanRequest0.userName, unbanRequest0.userId, unbanRequest0.text and unbanRequest0.createdAt.
        Same as cb.call(\"twitch.getUnbanRequests\", ...).
        """
    def twitch_get_user(self, user: Any) -> Any:
        """Looks up a Twitch user. Fails when Twitch is not connected.

        user: A Twitch login name (a leading @ is fine) or \"id:12345\", like \"coolviewer42\".
        Gives back: An object with id, login, displayName, type, broadcasterType, description, profileImageUrl, offlineImageUrl and createdAt. null when the user is not found.
        Same as cb.call(\"twitch.getUser\", ...).
        """
    def twitch_get_user_info(self, *, user: Any = None) -> Any:
        """Gets lots of details about a Twitch user: profile, account age, mod/VIP/sub status and their channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with targetFound, targetUser, targetUserName, targetUserId, targetDescription, targetProfileImageUrl, targetCreatedAt, targetAccountAgeDays, targetIsModerator, targetIsVip, targetGame, targetTitle and more.
        Same as cb.call(\"twitch.getUserInfo\", ...).
        """
    def twitch_get_videos(self, *, user: Any = None, type: Any = None, count: Any = None) -> Any:
        """Looks up the newest videos of a channel: past broadcasts, highlights or uploads. Handy for shoutouts (\"check out their last stream\"). Fails when Twitch is not connected.

        user: Optional. The channel, as a login name or id:12345. Leave empty for your own channel.
        type: Optional. Past broadcasts (archive), highlights, uploads, or all of them.
        count: Optional. How many videos to get, newest first (1 to 100).
        Gives back: An object with videoCount, videoTitle, videoUrl, videoCreatedAt, videoDuration, videoViews and video0.title.
        Same as cb.call(\"twitch.getVideos\", ...).
        """
    def twitch_get_vips(self) -> Any:
        """Lists every VIP of your Twitch channel. Fails when Twitch is not connected.

        Gives back: A list, each with userId, userLogin and userName.
        Same as cb.call(\"twitch.getVips\", ...).
        """
    def twitch_lock_prediction(self) -> Any:
        """Stops new bets on the running Twitch prediction. The result is decided later. Fails when Twitch is not connected.

        Gives back: An object with locked (true), active and the prediction's details: prediction.Id, prediction.Title, prediction.outcome0.title and so on.
        Same as cb.call(\"twitch.lockPrediction\", ...).
        """
    def twitch_pin_message(self, *, message_id: Any = None, duration: Any = None) -> Any:
        """Pins a message at the top of your Twitch chat. Fails when Twitch is not connected.

        message_id: Optional. The id of the message. Leave it out for the chat message that triggered the action (%messageId%).
        duration: Optional. How long to keep it pinned, in seconds (30 to 1800). Leave it out (or 0) to keep it until the stream ends.
        Gives back: Nothing.
        Same as cb.call(\"twitch.pinMessage\", ...).
        """
    def twitch_remove_blocked_term(self, term: Any) -> Any:
        """Removes a word or phrase from your Twitch blocked terms. Fails when Twitch is not connected.

        term: The blocked text (upper or lower case does not matter) or its id.
        Gives back: true when it was removed, false when the channel has no such blocked term.
        Same as cb.call(\"twitch.removeBlockedTerm\", ...).
        """
    def twitch_remove_moderator(self, *, user: Any = None) -> Any:
        """Takes moderator away from a viewer in your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.removeModerator\", ...).
        """
    def twitch_remove_suspicious_status(self, *, user: Any = None) -> Any:
        """Removes the suspicious mark from a viewer, so their messages show normally again. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with suspiciousUser, suspiciousUserName, suspiciousUserId, suspiciousUserType, suspiciousStatusResult and suspiciousStatus (\"NO_TREATMENT\").
        Same as cb.call(\"twitch.removeSuspiciousStatus\", ...).
        """
    def twitch_remove_tag(self, tag: Any) -> Any:
        """Removes one tag from your Twitch channel. Upper or lower case and a leading # do not matter. Fails when Twitch is not connected.

        tag: The tag, like \"Cozy\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.removeTag\", ...).
        """
    def twitch_remove_vip(self, *, user: Any = None) -> Any:
        """Takes VIP away from a viewer in your Twitch channel. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.removeVip\", ...).
        """
    def twitch_reply_to_message(self, message: Any, *, message_id: Any = None, send_as: Any = None) -> Any:
        """Replies to a message in your Twitch chat, so it shows as an answer to it. Fails when Twitch is not connected.

        message: What to say, like \"Good question!\"
        message_id: Optional. The id of the message to answer. Leave it out for the chat message that triggered the action (%messageId%).
        send_as: Optional. Who sends it: \"Default\" (your bot account when it is signed in, otherwise you), \"Bot\" or \"Broadcaster\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.replyToMessage\", ...).
        """
    def twitch_reset_reward_counter(self, *, reward: Any = None) -> Any:
        """Sets the total redeem count of a channel point reward back to 0. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: Nothing.
        Same as cb.call(\"twitch.resetRewardCounter\", ...).
        """
    def twitch_reset_reward_user_counters(self, *, reward: Any = None) -> Any:
        """Sets every viewer's redeem count of a channel point reward back to 0. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: Nothing.
        Same as cb.call(\"twitch.resetRewardUserCounters\", ...).
        """
    def twitch_reset_user_reward_counter(self, reward: Any, user: Any) -> Any:
        """Sets one viewer's redeem count of one channel point reward back to 0. Fails when the reward or the user is not found. Fails when Twitch is not connected.

        reward: The reward's title or id, like \"Hydrate!\".
        user: A Twitch login name (a leading @ is fine) or \"id:12345\", like \"coolviewer42\".
        Gives back: true.
        Same as cb.call(\"twitch.resetUserRewardCounter\", ...).
        """
    def twitch_reset_user_reward_counters(self, user: Any) -> Any:
        """Sets one viewer's redeem counts of every channel point reward back to 0. Fails when the user is not found. Fails when Twitch is not connected.

        user: A Twitch login name (a leading @ is fine) or \"id:12345\", like \"coolviewer42\".
        Gives back: How many counters were reset.
        Same as cb.call(\"twitch.resetUserRewardCounters\", ...).
        """
    def twitch_resolve_automod(self, *, message_id: Any = None, decision: Any = None) -> Any:
        """Allows or denies a chat message that AutoMod held back. Fails when Twitch is not connected.

        message_id: Optional. The id of the held message. Leave it out for %messageId% of the AutoMod trigger.
        decision: Optional. \"Allow\" (the default) shows the message; \"Deny\" throws it away. Capital letters do not matter.
        Gives back: Nothing.
        Same as cb.call(\"twitch.resolveAutomod\", ...).
        """
    def twitch_resolve_prediction(self, winner: Any, *, title: Any = None) -> Any:
        """Picks the winning outcome of your Twitch prediction and pays out the points. Fails when Twitch is not connected.

        winner: The winning outcome: its title, like \"Yes\", or its number (1, 2, ...).
        title: Optional. Only resolve a prediction with this title. Leave it out for the latest prediction.
        Gives back: An object with success (true), the prediction's details (prediction.Id, prediction.Title, prediction.outcome0.title and so on) and prediction.winningOutcome.title, .users and .points.
        Same as cb.call(\"twitch.resolvePrediction\", ...).
        """
    def twitch_resolve_unban_request(self, *, request_id: Any = None, decision: Any = None, message: Any = None) -> Any:
        """Approves or denies an unban request. Approving lets the person chat again. Fails when Twitch is not connected.

        request_id: Optional. The request to answer. The Unban request created trigger and Get unban requests give you this id.
        decision: Optional. Approve lets them back in; deny keeps the ban.
        message: Optional. An optional note the person sees, up to 500 characters.
        Gives back: An object with unbanRequestStatus.
        Same as cb.call(\"twitch.resolveUnbanRequest\", ...).
        """
    def twitch_run_ad(self, *, length: Any = None) -> Any:
        """Starts an ad break on your Twitch stream right now. Fails when Twitch is not connected.

        length: Optional. How long, in seconds: 30, 60, 90, 120, 150 or 180. Leave it out for 30.
        Gives back: An object with adLength and adRetryAfter (seconds until you can run another one).
        Same as cb.call(\"twitch.runAd\", ...).
        """
    def twitch_search_channels(self, *, query: Any = None, live_only: Any = None, count: Any = None) -> Any:
        """Searches Twitch for channels by name. Fails when Twitch is not connected.

        query: Optional. The name, or part of it, to search for.
        live_only: Optional. Tick to find only channels that are live right now.
        count: Optional. How many results to get (1 to 100).
        Gives back: An object with channelCount, channel0.login, channel0.name, channel0.isLive, channel0.title, channel0.gameName and channel0.startedAt.
        Same as cb.call(\"twitch.searchChannels\", ...).
        """
    def twitch_send_and_pin_message(self, message: Any, *, duration: Any = None) -> Any:
        """Sends a message in your Twitch chat as yourself (not the bot) and pins it right away. Fails when the message is empty. Fails when Twitch is not connected.

        message: What to say and pin, like \"Giveaway: type !join\"
        duration: Optional. How long to keep it pinned, in seconds. Leave it out (or 0) to keep it until the stream ends.
        Gives back: The id of the new message.
        Same as cb.call(\"twitch.sendAndPinMessage\", ...).
        """
    def twitch_send_announcement(self, message: Any, *, color: Any = None) -> Any:
        """Posts an announcement in your Twitch chat: a highlighted message with a coloured bar. Fails when Twitch is not connected.

        message: What to announce, like \"Giveaway starts in 5 minutes!\"
        color: Optional. \"primary\" (your channel colour, the default), \"blue\", \"green\", \"orange\" or \"purple\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.sendAnnouncement\", ...).
        """
    def twitch_send_guest_star_invite(self, *, user: Any = None, session_id: Any = None) -> Any:
        """Invites a viewer to your Twitch Guest Star session. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.sendGuestStarInvite\", ...).
        """
    def twitch_send_message(self, message: Any, *, send_as: Any = None) -> Any:
        """Sends a message in your Twitch chat. Empty text is skipped. Fails when Twitch is not connected.

        message: What to say, like \"Thanks for the follow!\"
        send_as: Optional. Who sends it: \"Default\" (your bot account when it is signed in, otherwise you), \"Bot\" or \"Broadcaster\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.sendMessage\", ...).
        """
    def twitch_send_shoutout(self, *, user: Any = None) -> Any:
        """Sends an official Twitch shoutout to another channel, so your viewers can follow them with one click. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with shoutoutSuccess (true).
        Same as cb.call(\"twitch.sendShoutout\", ...).
        """
    def twitch_send_whisper(self, message: Any, *, user: Any = None, send_as: Any = None) -> Any:
        """Sends a private whisper to a Twitch viewer. The sending account needs a verified phone number on Twitch. Fails when Twitch is not connected.

        message: What to whisper, like \"Your code is 1234\".
        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        send_as: Optional. Who sends it: \"Default\" (your bot account when it is signed in, otherwise you), \"Bot\" or \"Broadcaster\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.sendWhisper\", ...).
        """
    def twitch_set_automod_level(self, *, level: Any = None) -> Any:
        """Sets how strict AutoMod is for every kind of message at once. For example, turn it up when a raid arrives. Fails when Twitch is not connected.

        level: Optional. 0 turns AutoMod off; 4 holds the most messages for review.
        Gives back: An object with automodLevel.
        Same as cb.call(\"twitch.setAutomodLevel\", ...).
        """
    def twitch_set_branded_content(self, *, enabled: Any = None) -> Any:
        """Turns the branded content (sponsored stream) label on your Twitch channel on or off. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        Gives back: An object with success (true).
        Same as cb.call(\"twitch.setBrandedContent\", ...).
        """
    def twitch_set_content_label(self, *, label: Any = None, enabled: Any = None) -> Any:
        """Turns one content warning label on your Twitch channel on or off, like \"Strong language\". Fails when Twitch is not connected.

        label: Optional. \"DrugsIntoxication\", \"SexualThemes\", \"ViolentGraphic\", \"Gambling\" or \"ProfanityVulgarity\" (strong language, the default).
        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setContentLabel\", ...).
        """
    def twitch_set_game(self, game: Any) -> Any:
        """Changes the category (game) of your Twitch stream. It picks the exact name, or else the best search match. Fails when nothing matches. Fails when Twitch is not connected.

        game: The category or game name, like \"Just Chatting\".
        Gives back: An object with gameId, gameName, gameBoxArt and gameSuccess (true).
        Same as cb.call(\"twitch.setGame\", ...).
        """
    def twitch_set_reward_color(self, *, reward: Any = None, color: Any = None, random: Any = None) -> Any:
        """Changes the background colour of a channel point reward. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        color: Optional. The colour as #RRGGBB, like \"#FF6600\". Leave it out for Twitch purple (#9147FF).
        random: Optional. true picks a random colour instead. Leave it out for false.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardColor\", ...).
        """
    def twitch_set_reward_cooldown(self, *, reward: Any = None, seconds: Any = None) -> Any:
        """Sets how long everyone must wait between redeems of a channel point reward. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        seconds: Optional. The wait in seconds, like 60. 0 (the default) turns the cooldown off.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardCooldown\", ...).
        """
    def twitch_set_reward_cost(self, *, reward: Any = None, cost: Any = None) -> Any:
        """Changes the price of a channel point reward. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        cost: Optional. The new price in channel points. Leave it out for 100.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardCost\", ...).
        """
    def twitch_set_reward_enabled(self, *, reward: Any = None, enabled: Any = None) -> Any:
        """Turns a channel point reward on or off. A turned-off reward is hidden from viewers. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        enabled: Optional. true shows it, false hides it. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardEnabled\", ...).
        """
    def twitch_set_reward_group_enabled(self, group: Any, *, state: Any = None) -> Any:
        """Turns every reward in a reward group on or off at once. Rewards Cuebot did not make are skipped. Fails when the group has no rewards. Fails when Twitch is not connected.

        group: The reward group name, as set on the Twitch page (Rewards tab), like \"Games\".
        state: Optional. \"Enabled\" (the default), \"Disabled\" or \"Toggle\" (on, unless one of them is already on).
        Gives back: An object with rewardsChanged and rewardsSkipped (rewards Cuebot could not change).
        Same as cb.call(\"twitch.setRewardGroupEnabled\", ...).
        """
    def twitch_set_reward_group_paused(self, group: Any, *, state: Any = None) -> Any:
        """Pauses or unpauses every reward in a reward group at once. Rewards Cuebot did not make are skipped. Fails when the group has no rewards. Fails when Twitch is not connected.

        group: The reward group name, as set on the Twitch page (Rewards tab), like \"Games\".
        state: Optional. \"Paused\" (the default), \"Unpaused\" or \"Toggle\" (paused, unless one of them is already paused).
        Gives back: An object with rewardsChanged and rewardsSkipped (rewards Cuebot could not change).
        Same as cb.call(\"twitch.setRewardGroupPaused\", ...).
        """
    def twitch_set_reward_max_per_stream(self, *, reward: Any = None, max: Any = None, additive: Any = None) -> Any:
        """Sets how many times a channel point reward can be redeemed per stream, by everyone together. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        max: Optional. The limit, like 10. 0 (the default) removes the limit.
        additive: Optional. true adds max to the current limit (a negative number lowers it) instead of replacing it. Leave it out for false.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardMaxPerStream\", ...).
        """
    def twitch_set_reward_max_per_user(self, *, reward: Any = None, max: Any = None, additive: Any = None) -> Any:
        """Sets how many times each viewer can redeem a channel point reward per stream. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        max: Optional. The limit, like 1. 0 (the default) removes the limit.
        additive: Optional. true adds max to the current limit (a negative number lowers it) instead of replacing it. Leave it out for false.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardMaxPerUser\", ...).
        """
    def twitch_set_reward_paused(self, *, reward: Any = None, paused: Any = None) -> Any:
        """Pauses or unpauses a channel point reward. Viewers see a paused reward but cannot redeem it. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        paused: Optional. true pauses it, false unpauses it. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardPaused\", ...).
        """
    def twitch_set_reward_prompt(self, prompt: Any, *, reward: Any = None) -> Any:
        """Changes the text viewers see under a channel point reward. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        prompt: The new text, like \"Make me drink some water\".
        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardPrompt\", ...).
        """
    def twitch_set_reward_title(self, title: Any, *, reward: Any = None) -> Any:
        """Changes the name of a channel point reward. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        title: The new name, like \"Drink water!\".
        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setRewardTitle\", ...).
        """
    def twitch_set_shield_mode(self, *, enabled: Any = None) -> Any:
        """Turns Twitch Shield Mode on or off. It locks chat down quickly during a hate raid. Fails when Twitch is not connected.

        enabled: Optional. true turns Shield Mode on, false turns it off. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setShieldMode\", ...).
        """
    def twitch_set_sub_counter(self, *, count: Any = None, rollover_count: Any = None) -> Any:
        """Sets Cuebot's sub counter to a number, for example to start a new sub goal.

        count: Optional. The new count, a whole number of 0 or more. Leave it out for 0.
        rollover_count: Optional. The new rollover count. Leave it out to keep it.
        Gives back: An object with setResult (false when count is not a valid number) and, when it worked, subCounter.count, subCounter.rollover and subCounter.rolloverCount.
        Same as cb.call(\"twitch.setSubCounter\", ...).
        """
    def twitch_set_tags(self, tags: Any) -> Any:
        """Replaces all tags of your Twitch channel. Twitch allows at most 10. Fails when Twitch is not connected.

        tags: The tags as text, separated by commas or spaces, like \"English, Chill, Cozy\".
        Gives back: Nothing.
        Same as cb.call(\"twitch.setTags\", ...).
        """
    def twitch_set_title(self, title: Any) -> Any:
        """Changes the title of your Twitch stream. Fails when Twitch is not connected.

        title: The new title, like \"Chill games with chat\".
        Gives back: An object with titleSuccess (true).
        Same as cb.call(\"twitch.setTitle\", ...).
        """
    def twitch_set_vacation(self, *, enabled: Any = None, start: Any = None, end: Any = None) -> Any:
        """Turns schedule vacation on or off. While on, the streams in that time are hidden from your schedule. Fails when Twitch is not connected.

        enabled: Optional. Tick to go on vacation; untick to end it.
        start: Optional. When the vacation starts, like 2026-12-20 00:00. Needed when turning it on.
        end: Optional. When it ends. Needed when turning it on.
        Gives back: Nothing.
        Same as cb.call(\"twitch.setVacation\", ...).
        """
    def twitch_slow_mode(self, *, enabled: Any = None, seconds: Any = None) -> Any:
        """Turns slow mode on or off: viewers must wait between messages. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        seconds: Optional. The wait between messages, in seconds (3 to 120). Leave it out for 30.
        Gives back: Nothing.
        Same as cb.call(\"twitch.slowMode\", ...).
        """
    def twitch_snooze_ad(self) -> Any:
        """Pushes your next scheduled Twitch ad back, if you have snoozes left. Fails when Twitch is not connected.

        Gives back: An object with snooze.success, nextAdAt, lastAdAt, adDuration, prerollFreeTime, snoozeCount, snoozeRefreshAt, prerollFree and prerollFreeSeconds.
        Same as cb.call(\"twitch.snoozeAd\", ...).
        """
    def twitch_start_raid(self, *, user: Any = None) -> Any:
        """Starts a raid to another Twitch channel. Fails when the channel is not found. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with startRaidSuccess (true).
        Same as cb.call(\"twitch.startRaid\", ...).
        """
    def twitch_subscriber_mode(self, *, enabled: Any = None) -> Any:
        """Turns subscriber-only chat on or off. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.subscriberMode\", ...).
        """
    def twitch_timeout(self, *, user: Any = None, duration: Any = None, reason: Any = None) -> Any:
        """Times out a viewer in your Twitch chat, so they cannot chat for a while. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        duration: Optional. How long, in seconds (1 second to 14 days). Leave it out for 600 (10 minutes).
        reason: Optional. Why, like \"Calm down\".
        Gives back: An object with timedOutUser (display name), timedOutUserName (login), timedOutUserId, timedOutUserType (\"twitch\") and timeoutResult (true).
        Same as cb.call(\"twitch.timeout\", ...).
        """
    def twitch_unban(self, *, user: Any = None) -> Any:
        """Lets a banned viewer chat in your Twitch channel again. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with unbannedUser (display name), unbannedUserName (login), unbannedUserId, unbannedUserType (\"twitch\") and unbanResult (true).
        Same as cb.call(\"twitch.unban\", ...).
        """
    def twitch_unblock_user(self, *, user: Any = None) -> Any:
        """Unblocks someone you blocked before. Fails when Twitch is not connected.

        user: Optional. Login name (a leading @ is fine) or id:12345. Leave empty for the person who triggered the action.
        Gives back: Nothing.
        Same as cb.call(\"twitch.unblockUser\", ...).
        """
    def twitch_unique_chat(self, *, enabled: Any = None) -> Any:
        """Turns unique chat on or off: viewers cannot post the same message twice in a row. Fails when Twitch is not connected.

        enabled: Optional. true turns it on, false turns it off. Leave it out for true.
        Gives back: Nothing.
        Same as cb.call(\"twitch.uniqueChat\", ...).
        """
    def twitch_unpin_message(self, *, message_id: Any = None) -> Any:
        """Unpins a message in your Twitch chat. Fails when nothing is pinned. Fails when Twitch is not connected.

        message_id: Optional. The id of the pinned message. Leave it out for the message pinned now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.unpinMessage\", ...).
        """
    def twitch_untimeout(self, *, user: Any = None) -> Any:
        """Ends a viewer's timeout early, so they can chat again. Fails when Twitch is not connected.

        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with unTimedOutUser (display name), unTimedOutUserName (login), unTimedOutUserId, unTimedOutUserType (\"twitch\") and unTimedOutResult (true).
        Same as cb.call(\"twitch.untimeout\", ...).
        """
    def twitch_update_guest_star_settings(self, *, slot_count: Any = None, layout: Any = None, moderator_send_live: Any = None, browser_source_audio: Any = None, regenerate_browser_sources: Any = None) -> Any:
        """Changes your Twitch Guest Star settings. Anything left at its default stays as it is. Fails when Twitch is not connected.

        slot_count: Optional. How many guest slots, 1 to 6. Leave it out (or 0) to keep it.
        layout: Optional. \"TILED_LAYOUT\", \"SCREENSHARE_LAYOUT\", \"HORIZONTAL_LAYOUT\", \"VERTICAL_LAYOUT\" or \"Keep\" (the default).
        moderator_send_live: Optional. Whether moderators can send guests live: \"On\", \"Off\" or \"Keep\" (the default).
        browser_source_audio: Optional. Whether the browser sources play audio: \"On\", \"Off\" or \"Keep\" (the default).
        regenerate_browser_sources: Optional. true makes new browser source links; the old ones stop working. Leave it out for false.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updateGuestStarSettings\", ...).
        """
    def twitch_update_guest_star_slot(self, *, slot: Any = None, to_slot: Any = None, session_id: Any = None) -> Any:
        """Moves a Twitch Guest Star guest from one slot to another. Fails when Twitch is not connected.

        slot: Optional. The slot the guest is in now, like \"1\". Leave it out for 1.
        to_slot: Optional. The slot to move them to, like \"3\". Leave it out for 1.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updateGuestStarSlot\", ...).
        """
    def twitch_update_guest_star_slot_settings(self, *, slot: Any = None, audio: Any = None, video: Any = None, live: Any = None, volume: Any = None, session_id: Any = None) -> Any:
        """Changes audio, video, live state or volume of one Twitch Guest Star slot. Fails when Twitch is not connected.

        slot: Optional. The slot number, from 1 up to the number of slots, like \"2\". Leave it out for 1.
        audio: Optional. The guest's audio: \"On\", \"Off\" or \"Keep\" (the default, leaves it as it is).
        video: Optional. The guest's video: \"On\", \"Off\" or \"Keep\" (the default, leaves it as it is).
        live: Optional. Whether the guest is live on stream: \"On\", \"Off\" or \"Keep\" (the default, leaves it as it is).
        volume: Optional. Volume from 0 to 100. Leave it out (or -1) to keep it.
        session_id: Optional. The Guest Star session id. Leave it out for the session running now.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updateGuestStarSlotSettings\", ...).
        """
    def twitch_update_pinned_duration(self, *, message_id: Any = None, duration: Any = None) -> Any:
        """Changes how long the pinned message stays pinned, counting from now. Fails when Twitch is not connected.

        message_id: Optional. The id of the pinned message. Leave it out for the message pinned now.
        duration: Optional. Seconds from now (30 to 1800). 0 keeps it until the stream ends. Leave it out for 60.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updatePinnedDuration\", ...).
        """
    def twitch_update_redemption(self, *, reward_id: Any = None, redemption_id: Any = None, status: Any = None) -> Any:
        """Marks a channel point redemption as done, or cancels it and gives the points back. Only works for rewards Cuebot made. Fails when Twitch is not connected.

        reward_id: Optional. The reward id. Leave it out for %rewardId% of the redemption trigger.
        redemption_id: Optional. The redemption id. Leave it out for %redemptionId% of the redemption trigger.
        status: Optional. \"Fulfil\" (the default) marks it done; \"Cancel\" refunds the points. Capital letters do not matter.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updateRedemption\", ...).
        """
    def twitch_update_reward(self, *, reward: Any = None, title: Any = None, prompt: Any = None, cost: Any = None, operator: Any = None) -> Any:
        """Changes the name, text and price of a channel point reward in one go. Empty values stay as they are. Twitch only lets Cuebot change rewards that Cuebot made. Fails when Twitch is not connected.

        reward: Optional. The reward's title or id, like \"Hydrate\". Leave it out for the reward that was just redeemed.
        title: Optional. The new name. Leave it empty to keep it.
        prompt: Optional. The new text. Leave it empty to keep it.
        cost: Optional. A number. With \"Set\" it becomes the new price; with the other operators it changes the current price (never below 1).
        operator: Optional. \"Set\" (the default), \"Add\", \"Subtract\", \"Multiply\" or \"Divide\".
        Gives back: An object with rewardCost (the price now).
        Same as cb.call(\"twitch.updateReward\", ...).
        """
    def twitch_update_stream_info(self, *, title: Any = None, game: Any = None, tags: Any = None) -> Any:
        """Changes the title, category and tags of your Twitch stream in one go. Anything you leave empty stays as it is. Fails when Twitch is not connected.

        title: Optional. The new title. Leave it empty to keep it.
        game: Optional. The new category or game name, like \"Minecraft\". Leave it empty to keep it.
        tags: Optional. New tags as text, separated by commas, like \"English, Chill\". Leave it empty to keep them.
        Gives back: Nothing.
        Same as cb.call(\"twitch.updateStreamInfo\", ...).
        """
    def twitch_warn(self, reason: Any, *, user: Any = None) -> Any:
        """Sends a viewer an official Twitch warning. Fails when Twitch is not connected.

        reason: The warning text they will see, like \"Please keep it friendly\".
        user: Optional. A Twitch login name (a leading @ is fine) or \"id:12345\". Leave it out for the user who triggered the action.
        Gives back: An object with warnedUser (display name), warnedUserName (login), warnedUserId, warnedUserType (\"twitch\") and warnedResult (true).
        Same as cb.call(\"twitch.warn\", ...).
        """
    def user_vars_all(self, *, platform: Any = None, user_id: Any = None, user_name: Any = None) -> Any:
        """Gives you every variable saved for one viewer. Leave the user settings out for the person who triggered the action.

        platform: Optional. The viewer's platform, like \"twitch\", \"youtube\" or \"kick\". Leave it out for the platform of the trigger.
        user_id: Optional. The viewer's id on that platform.
        user_name: Optional. The viewer's name. Cuebot looks it up among viewers it has seen before.
        Gives back: An object with every variable name and its value. Empty when no viewer was found.
        Same as cb.call(\"userVars.all\", ...).
        """
    def user_vars_clear_temporary(self, *, platform: Any = None) -> Any:
        """Deletes every temporary user variable (the ones that are not saved), for all viewers.

        platform: Optional. Only clear viewers of this platform, like \"twitch\". Leave it out for every platform.
        Gives back: How many variables were deleted.
        Same as cb.call(\"userVars.clearTemporary\", ...).
        """
    def user_vars_delete(self, name: Any, *, platform: Any = None, user_id: Any = None, user_name: Any = None) -> Any:
        """Deletes one variable of one viewer. Leave the user settings out for the person who triggered the action.

        name: The variable name, like \"lastSeen\".
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user_id: Optional. The viewer's id on that platform.
        user_name: Optional. The viewer's name. Cuebot looks it up among viewers it has seen before.
        Gives back: true when the variable existed, otherwise false (also when no viewer was found).
        Same as cb.call(\"userVars.delete\", ...).
        """
    def user_vars_delete_for_all_users(self, name: Any, *, platform: Any = None) -> Any:
        """Deletes one variable from every viewer at once. Handy to reset a weekly score.

        name: The variable name, like \"weeklyScore\".
        platform: Optional. Only viewers of this platform, like \"twitch\". Leave it out for every platform.
        Gives back: How many viewers lost the variable.
        Same as cb.call(\"userVars.deleteForAllUsers\", ...).
        """
    def user_vars_get(self, name: Any, *, platform: Any = None, user_id: Any = None, user_name: Any = None, default: Any = None) -> Any:
        """Reads a variable saved for one viewer. Leave the user settings out for the person who triggered the action.

        name: The variable name, like \"wins\".
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user_id: Optional. The viewer's id on that platform.
        user_name: Optional. The viewer's name. Cuebot looks it up among viewers it has seen before.
        default: Optional. What to give back when the viewer has no such variable, like 0.
        Gives back: The value, or your default (null if you gave none) when it is missing.
        Same as cb.call(\"userVars.get\", ...).
        """
    def user_vars_increment(self, name: Any, *, amount: Any = None, persisted: Any = None, platform: Any = None, user_id: Any = None, user_name: Any = None) -> Any:
        """Adds to a number saved for one viewer, like their win count. A missing variable starts at 0. Fails when no viewer can be found.

        name: The variable name, like \"wins\".
        amount: Optional. How much to add, like 1. Use a negative number to subtract. Leave it out to add 1.
        persisted: Optional. true (the default) saves it so it is still there after a restart; false keeps it only until Cuebot closes.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user_id: Optional. The viewer's id on that platform.
        user_name: Optional. The viewer's name. Cuebot looks it up among viewers it has seen before.
        Gives back: The new value.
        Same as cb.call(\"userVars.increment\", ...).
        """
    def user_vars_set(self, name: Any, value: Any, *, persisted: Any = None, platform: Any = None, user_id: Any = None, user_name: Any = None) -> Any:
        """Saves a value for one viewer, like their favourite game. Leave the user settings out for the person who triggered the action. Fails when no viewer can be found.

        name: The variable name, like \"favouriteGame\". No spaces.
        value: The value to store: text, a number or true/false.
        persisted: Optional. true (the default) saves it so it is still there after a restart; false keeps it only until Cuebot closes.
        platform: Optional. The viewer's platform, like \"twitch\". Leave it out for the platform of the trigger.
        user_id: Optional. The viewer's id on that platform.
        user_name: Optional. The viewer's name. Cuebot looks it up among viewers it has seen before.
        Gives back: Nothing.
        Same as cb.call(\"userVars.set\", ...).
        """
    def voice_control_commands(self) -> Any:
        """Lists all your voice commands from the Voice Control page.

        Gives back: A list of commands. Each has id, name, enabled, phrase (what to say), location (Exact, Start or Anywhere), stopAfter, overrideGlobal and confidenceThreshold (0-100).
        Same as cb.call(\"voiceControl.commands\", ...).
        """
    def voice_control_dictation_log(self) -> Any:
        """Shows what voice control heard recently. Handy to see why a command did not run.

        Gives back: A list of heard phrases. Each has time, text, confidencePercent (how sure it was, 0-100), commands (which ones ran) and ignored.
        Same as cb.call(\"voiceControl.dictationLog\", ...).
        """
    def voice_control_input_devices(self) -> Any:
        """Lists the microphones voice control can listen to.

        Gives back: A list of microphones. Each has id, name and isDefault (true for your computer's default).
        Same as cb.call(\"voiceControl.inputDevices\", ...).
        """
    def voice_control_is_listening(self) -> Any:
        """Checks whether voice control is listening to your microphone right now.

        Gives back: true while listening, otherwise false.
        Same as cb.call(\"voiceControl.isListening\", ...).
        """
    def voice_control_set_command(self, command: Any, phrase: Any, *, location: Any = None, create: Any = None) -> Any:
        """Changes what you have to say for a voice command. Can also make a new command. Fails with an error when the command is not found (and create is off) or the phrase is empty.

        command: The voice command's name or id, like \"Switch to gameplay\".
        phrase: What you have to say, like \"go to gameplay\". Needed.
        location: Optional. Where in what you say the phrase must be: \"Exact\" (all of it), \"Start\" or \"Anywhere\". Leave it out to keep it as it is.
        create: Optional. true makes a new command with this name when none exists. Default false.
        Gives back: The command after the change, with the same details as voiceControl.commands.
        Same as cb.call(\"voiceControl.setCommand\", ...).
        """
    def voice_control_set_command_state(self, command: Any, *, state: Any = None) -> Any:
        """Turns one voice command on or off. Fails with an error when no command has that name.

        command: The voice command's name or id, like \"Switch to gameplay\".
        state: Optional. \"Enabled\", \"Disabled\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"voiceControl.setCommandState\", ...).
        """
    def voice_control_set_input_device(self, device: Any) -> Any:
        """Picks which microphone voice control listens to. The choice is saved.

        device: The microphone's name or id from voiceControl.inputDevices. Empty or \"Default\" uses your computer's default microphone.
        Gives back: true when done.
        Same as cb.call(\"voiceControl.setInputDevice\", ...).
        """
    def voice_control_start_listening(self) -> Any:
        """Starts listening to your microphone for voice commands. Fails with an error when no microphone or recognizer is available.

        Gives back: true when it is listening now.
        Same as cb.call(\"voiceControl.startListening\", ...).
        """
    def voice_control_status(self) -> Any:
        """Tells you how voice control is doing right now.

        Gives back: engine (the speech recognizer in use), state, message (a status line), listening (true/false) and inputDevice (the chosen microphone; empty means the default).
        Same as cb.call(\"voiceControl.status\", ...).
        """
    def voice_control_stop_listening(self) -> Any:
        """Stops listening for voice commands.

        Gives back: true when done.
        Same as cb.call(\"voiceControl.stopListening\", ...).
        """
    def voicemod_get_current_voice(self) -> Any:
        """Asks Voicemod which voice is on right now.

        Gives back: id and name of the current voice.
        Same as cb.call(\"voicemod.getCurrentVoice\", ...).
        """
    def voicemod_get_sounds(self) -> Any:
        """Lists all the sounds on your Voicemod soundboard.

        Gives back: A list of sounds. Each has name and fileName.
        Same as cb.call(\"voicemod.getSounds\", ...).
        """
    def voicemod_get_state(self, switch: Any) -> Any:
        """Tells you the last known state of one Voicemod switch. It does not ask Voicemod again.

        switch: Which switch: \"VoiceChanger\", \"HearMyself\", \"BackgroundEffects\" or \"MuteMic\".
        Gives back: true for on, false for off, or null when Cuebot does not know yet. Fails with an error for any other switch name.
        Same as cb.call(\"voicemod.getState\", ...).
        """
    def voicemod_get_voices(self) -> Any:
        """Lists all your Voicemod voices.

        Gives back: A list of voices. Each has id, name and favorite (true when you starred it).
        Same as cb.call(\"voicemod.getVoices\", ...).
        """
    def voicemod_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Voicemod right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"voicemod.isConnected\", ...).
        """
    def voicemod_play_sound(self, sound: Any) -> Any:
        """Plays a sound from your Voicemod soundboard. Fails with an error when no sound has that name.

        sound: The sound's name as Voicemod shows it, like \"Air Horn\", or its file name.
        Gives back: The name of the sound that played.
        Same as cb.call(\"voicemod.playSound\", ...).
        """
    def voicemod_random_voice(self, *, mode: Any = None) -> Any:
        """Switches Voicemod to a random voice.

        mode: Optional. Which voices to pick from: \"AllVoices\", \"FreeVoices\", \"FavoriteVoices\" or \"CustomVoices\". Default \"AllVoices\".
        Gives back: true when done.
        Same as cb.call(\"voicemod.randomVoice\", ...).
        """
    def voicemod_select_voice(self, voice: Any) -> Any:
        """Switches Voicemod to another voice. Fails with an error when no voice has that name or Voicemod is not connected.

        voice: The voice's name as Voicemod shows it, like \"Robot\", or its id.
        Gives back: The name of the voice that is now on.
        Same as cb.call(\"voicemod.selectVoice\", ...).
        """
    def voicemod_set_background_effects(self, *, state: Any = None) -> Any:
        """Turns Voicemod's background effects (the sounds behind some voices) on or off.

        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"voicemod.setBackgroundEffects\", ...).
        """
    def voicemod_set_censor_beep(self, *, state: Any = None) -> Any:
        """Starts or stops Voicemod's censor beep, the bleep that covers bad words.

        state: Optional. \"On\" starts the beep, \"Off\" stops it. Default \"On\".
        Gives back: true when done.
        Same as cb.call(\"voicemod.setCensorBeep\", ...).
        """
    def voicemod_set_hear_myself(self, *, state: Any = None) -> Any:
        """Turns Voicemod's \"Hear myself\" on or off. When it is on, you hear your changed voice in your headphones.

        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"voicemod.setHearMyself\", ...).
        """
    def voicemod_set_mute(self, *, state: Any = None) -> Any:
        """Mutes or unmutes your microphone in Voicemod.

        state: Optional. \"On\" mutes, \"Off\" unmutes, \"Toggle\" switches. Default \"Toggle\".
        Gives back: The new state: true when muted, false when not.
        Same as cb.call(\"voicemod.setMute\", ...).
        """
    def voicemod_set_voice_changer(self, *, state: Any = None) -> Any:
        """Turns the Voicemod voice changer on or off. When it is off, your normal voice is heard.

        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"voicemod.setVoiceChanger\", ...).
        """
    def voicemod_stop_sounds(self) -> Any:
        """Stops every Voicemod soundboard sound that is playing.

        Gives back: true when done.
        Same as cb.call(\"voicemod.stopSounds\", ...).
        """
    def vtubestudio_color_tint(self, color: Any, *, name_contains: Any = None, mix: Any = None, revert_after: Any = None) -> Any:
        """Tints your model a color, optionally only the parts whose names contain some text, and optionally only for a while.

        color: The color, like \"#FF8080\".
        name_contains: Optional. Only parts whose ArtMesh names contain this text.
        mix: Optional. How much the color replaces scene lighting, from 0 to 1. The default is 1.
        revert_after: Optional. Seconds until the colors go back.
        Gives back: How many parts were tinted.
        Same as cb.call(\"vtubestudio.colorTint\", ...).
        """
    def vtubestudio_get_current_model(self) -> Any:
        """Tells you which model VTube Studio has loaded and where it is on screen. Fails with an error when VTube Studio is not connected.

        Gives back: loaded (true/false), name, id, positionX, positionY, rotation and size.
        Same as cb.call(\"vtubestudio.getCurrentModel\", ...).
        """
    def vtubestudio_get_expressions(self) -> Any:
        """Lists the face expressions of the model that is loaded now, and which ones are on. Fails with an error when VTube Studio is not connected.

        Gives back: A list of expressions. Each has name, file and active (true when it is on).
        Same as cb.call(\"vtubestudio.getExpressions\", ...).
        """
    def vtubestudio_get_hotkeys(self) -> Any:
        """Lists the hotkeys of the model that is loaded now. Fails with an error when VTube Studio is not connected.

        Gives back: A list of hotkeys. Each has id, name, type (what it does) and file.
        Same as cb.call(\"vtubestudio.getHotkeys\", ...).
        """
    def vtubestudio_inject_parameters(self, parameters: Any, *, hold_seconds: Any = None, mode: Any = None, face_found: Any = None) -> Any:
        """Sets tracking values on your model for a while, like tilting the head or opening the mouth. Your script waits until the time is over.

        parameters: The values as text, like \"FaceAngleX=20, MouthOpen=1\". You can also give an object, like {\"FaceAngleX\": 20}.
        hold_seconds: Optional. How many seconds to keep the values, 0 to 3600. Default 1.
        mode: Optional. \"add\" adds the values to the tracking. Anything else (the default) replaces the tracking values.
        face_found: Optional. true tells VTube Studio to act as if it sees your face while the values are sent. Starts as false.
        Gives back: true when done.
        Same as cb.call(\"vtubestudio.injectParameters\", ...).
        """
    def vtubestudio_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to VTube Studio and allowed in as a plugin.

        Gives back: true while connected and allowed, otherwise false.
        Same as cb.call(\"vtubestudio.isConnected\", ...).
        """
    def vtubestudio_load_item(self, file_name: Any, *, image: Any = None, position_x: Any = None, position_y: Any = None, size: Any = None, pin: Any = None, unload_after: Any = None) -> Any:
        """Puts an item (or a picture) into the scene.

        file_name: The item file, like \"star.png\". Leave it out when you give an image.
        image: Optional. A picture to load instead: a web address, a file or base64.
        position_x: Optional. -1 (left edge) to 1 (right edge). The default is 0.
        position_y: Optional. -1 (bottom) to 1 (top). The default is 0.
        size: Optional. 0 to 1. The default is 0.32.
        pin: Optional. None, Random, ArtMeshCenter, ArtMeshRandom or LastClick. The default is None.
        unload_after: Optional. Seconds until it is removed again.
        Gives back: The new item's id.
        Same as cb.call(\"vtubestudio.loadItem\", ...).
        """
    def vtubestudio_load_model(self, model: Any) -> Any:
        """Switches VTube Studio to another model. Fails with an error when no model has that name or VTube Studio is not connected.

        model: The model's name as VTube Studio shows it, like \"Akari\", or its id.
        Gives back: The id of the model that was loaded.
        Same as cb.call(\"vtubestudio.loadModel\", ...).
        """
    def vtubestudio_load_vfx_preset(self, preset: Any, *, fade_time: Any = None, revert_after: Any = None) -> Any:
        """Turns on one of your visual effects presets, optionally only for a while.

        preset: The preset name.
        fade_time: Optional. Seconds the change takes (0-2). The default is 0.5.
        revert_after: Optional. Seconds until the previous effects come back.
        Gives back: The preset now in use.
        Same as cb.call(\"vtubestudio.loadVfxPreset\", ...).
        """
    def vtubestudio_move_model(self, *, time: Any = None, relative: Any = None, position_x: Any = None, position_y: Any = None, rotation: Any = None, size: Any = None) -> Any:
        """Moves, turns or resizes your model on screen. Values you leave out stay as they are. Fails with an error when VTube Studio is not connected.

        time: Optional. How long the move takes, 0 to 2 seconds. Default 0.5.
        relative: Optional. true moves the model by these amounts from where it is now. false (default) moves it to these exact values.
        position_x: Optional. Left/right position: -1 is the left edge, 1 is the right edge.
        position_y: Optional. Up/down position: -1 is the bottom edge, 1 is the top edge.
        rotation: Optional. How far to turn the model, in degrees, like 15.
        size: Optional. The model's size, from -100 (small) to 100 (big).
        Gives back: true when done.
        Same as cb.call(\"vtubestudio.moveModel\", ...).
        """
    def vtubestudio_physics(self, *, strength: Any = None, wind: Any = None, seconds: Any = None) -> Any:
        """Turns up the physics or the wind for a while.

        strength: Optional. Physics strength from 0 to 100.
        wind: Optional. Wind from 0 to 100.
        seconds: Optional. How long. The default is 5.
        Gives back: true
        Same as cb.call(\"vtubestudio.physics\", ...).
        """
    def vtubestudio_request(self, message_type: Any, *, data: Any = None) -> Any:
        """Sends any VTube Studio API request and gives back its answer. For advanced users who know VTube Studio's request names.

        message_type: The request name, like \"StatisticsRequest\". Needed.
        data: Optional. The request's settings as an object, like {\"hotkeyID\": \"Wave\"}.
        Gives back: VTube Studio's answer data, or null when it sent none.
        Same as cb.call(\"vtubestudio.request\", ...).
        """
    def vtubestudio_set_expression(self, expression: Any, *, state: Any = None, fade_time: Any = None) -> Any:
        """Turns a face expression on your model on or off, like blush or angry eyes. Fails with an error when VTube Studio is not connected.

        expression: The expression's name or file, like \"Blush\" or \"Blush.exp3.json\".
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"On\".
        fade_time: Optional. How many seconds the change takes to fade in or out. Leave it out for 0.25 seconds.
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"vtubestudio.setExpression\", ...).
        """
    def vtubestudio_set_parameter(self, parameter: Any, value: Any, *, weight: Any = None, seconds: Any = None) -> Any:
        """Keeps a tracking parameter at a value (Cuebot keeps sending it) until it is set again or stopped.

        parameter: The parameter, like FaceAngleX or one you created.
        value: The value.
        weight: Optional. 0 to 1: how much it overrides face tracking.
        seconds: Optional. How long to keep it.
        Gives back: true
        Same as cb.call(\"vtubestudio.setParameter\", ...).
        """
    def vtubestudio_set_vfx(self, *, state: Any = None) -> Any:
        """Turns visual effects On, Off, or Toggle.

        state: Optional. On, Off or Toggle. The default is Toggle.
        Gives back: true when visual effects are now on.
        Same as cb.call(\"vtubestudio.setVfx\", ...).
        """
    def vtubestudio_stop_parameter(self, *, parameter: Any = None) -> Any:
        """Stops keeping a parameter set, so face tracking takes over again.

        parameter: Optional. The parameter; leave it out to stop all of them.
        Gives back: How many parameters were stopped.
        Same as cb.call(\"vtubestudio.stopParameter\", ...).
        """
    def vtubestudio_throw_items(self, *, files: Any = None, images: Any = None, count: Any = None, size: Any = None, flinch: Any = None) -> Any:
        """Throws items or pictures at your model; it flinches and they fall away. Fails with an error when VTube Studio is not connected.

        files: Optional. Item files to throw, separated by commas (give files or images).
        images: Optional. Pictures to throw instead (web addresses or files), separated by commas.
        count: Optional. How many to throw. The default is 1.
        size: Optional. The size from 0 to 1. The default is 0.15.
        flinch: Optional. How hard the model reacts, from 0 to 1. The default is 0.5.
        Gives back: How many were thrown.
        Same as cb.call(\"vtubestudio.throwItems\", ...).
        """
    def vtubestudio_trigger_hotkey(self, hotkey: Any, *, item_instance_id: Any = None) -> Any:
        """Runs one of your VTube Studio hotkeys, like a wave or an outfit change. Fails with an error when VTube Studio is not connected.

        hotkey: The hotkey's name as VTube Studio shows it, like \"Wave\", or its id.
        item_instance_id: Optional. The id of a Live2D item, to press that item's hotkey instead of the model's. Most people leave it out.
        Gives back: The id of the hotkey that ran.
        Same as cb.call(\"vtubestudio.triggerHotkey\", ...).
        """
    def wavelink_add_app_to_channel(self, channel: Any, *, app: Any = None) -> Any:
        """Sends an app's sound to a Wave Link channel, like putting Spotify on the Music channel. Only for Wave Link 3. Fails with an error when Wave Link is not connected.

        channel: The channel, by name or id, like \"Music\" or \"Game\".
        app: Optional. The app, by name or id, like \"Spotify\". Leave it empty for the app that is in front right now.
        Gives back: The name of the app that was moved.
        Same as cb.call(\"wavelink.addAppToChannel\", ...).
        """
    def wavelink_add_output_to_mix(self, device: Any, mix: Any) -> Any:
        """Plays a Wave Link mix on an output device. Only for Wave Link 3. Fails with an error when Wave Link is not connected.

        device: The output device, by name or id, like \"Headphones\".
        mix: The mix, by name or id, like \"Stream Mix\" or \"Monitor Mix\".
        Gives back: true when done.
        Same as cb.call(\"wavelink.addOutputToMix\", ...).
        """
    def wavelink_connect(self) -> Any:
        """Looks for Wave Link and starts connecting. It does not wait: Cuebot keeps trying in the background. Wave Link only runs on Windows and macOS.

        Gives back: true when Wave Link can run on this computer (connecting has started), false when it cannot (for example on Linux).
        Same as cb.call(\"wavelink.connect\", ...).
        """
    def wavelink_disconnect(self) -> Any:
        """Disconnects Cuebot from Wave Link.

        Gives back: true when done.
        Same as cb.call(\"wavelink.disconnect\", ...).
        """
    def wavelink_get_channels(self) -> Any:
        """Lists your Wave Link channels (in Wave Link 1.x/2.x: inputs) and how they are set.

        Gives back: A list of channels. Each has id, name, type, muted, volume, mixes (mute, volume and filterBypassed per mix), effects (name, enabled) and apps.
        Same as cb.call(\"wavelink.getChannels\", ...).
        """
    def wavelink_get_input_devices(self) -> Any:
        """Lists your Wave Link input devices (in Wave Link 1.x/2.x: microphones) and their settings.

        Gives back: A list of devices. Each has id, name, type and inputs (muted, gain, gainDb, gainLocked, micPcMix, lowCut, clipGuard).
        Same as cb.call(\"wavelink.getInputDevices\", ...).
        """
    def wavelink_get_mixes(self) -> Any:
        """Lists your Wave Link mixes, like Stream Mix and Monitor Mix.

        Gives back: A list of mixes. Each has id, name, muted and volume.
        Same as cb.call(\"wavelink.getMixes\", ...).
        """
    def wavelink_get_output_devices(self) -> Any:
        """Lists your Wave Link output devices, like headphones and speakers.

        Gives back: main (the main output's name), monitoredMix (the mix you hear) and devices (each with id, name and outputs: muted, volume, mixId).
        Same as cb.call(\"wavelink.getOutputDevices\", ...).
        """
    def wavelink_get_status(self) -> Any:
        """Tells you about the Wave Link connection and what Cuebot found in it.

        Gives back: isConnected, isAvailable, status (a status line), api (\"3\" for Wave Link 3, \"legacy\" for 1.x/2.x), port, interfaceRevision and how many channels, mixes, inputDevices and outputDevices there are.
        Same as cb.call(\"wavelink.getStatus\", ...).
        """
    def wavelink_is_connected(self) -> Any:
        """Checks whether Cuebot is connected to Elgato Wave Link right now.

        Gives back: true while connected, otherwise false.
        Same as cb.call(\"wavelink.isConnected\", ...).
        """
    def wavelink_set_channel_effect(self, channel: Any, effect: Any, *, state: Any = None) -> Any:
        """Turns an effect on a Wave Link channel on or off (in Wave Link 1.x/2.x: an input filter). Fails with an error when Wave Link is not connected.

        channel: The channel, by name or id, like \"Music\" or \"Game\".
        effect: The effect or filter, by name or id, like \"Noise Gate\".
        state: Optional. \"Enabled\", \"Disabled\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"wavelink.setChannelEffect\", ...).
        """
    def wavelink_set_channel_mute(self, channel: Any, *, mix: Any = None, state: Any = None) -> Any:
        """Mutes or unmutes a Wave Link channel. Fails with an error when Wave Link is not connected.

        channel: The channel, by name or id, like \"Music\" or \"Game\".
        mix: Optional. Only in this mix, like \"Stream Mix\". Leave it empty for the whole channel (Wave Link 1.x/2.x: both mixes).
        state: Optional. \"Muted\", \"NotMuted\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true when muted, false when not.
        Same as cb.call(\"wavelink.setChannelMute\", ...).
        """
    def wavelink_set_channel_volume(self, channel: Any, volume: Any, *, mix: Any = None, relative: Any = None) -> Any:
        """Changes the volume of a Wave Link channel. Fails with an error when Wave Link is not connected.

        channel: The channel, by name or id, like \"Music\" or \"Game\".
        volume: The volume in percent, 0-100, like 50.
        mix: Optional. Only in this mix, like \"Stream Mix\". Leave it empty for the channel itself.
        relative: Optional. true adds the number to the current value instead (use a minus number to go down). Default false.
        Gives back: The new volume.
        Same as cb.call(\"wavelink.setChannelVolume\", ...).
        """
    def wavelink_set_filter_bypass(self, channel: Any, *, mix: Any = None, state: Any = None) -> Any:
        """Skips (bypasses) all filters on a Wave Link input, or turns them back on. Only for Wave Link 1.x/2.x. Fails with an error when Wave Link is not connected.

        channel: The channel, by name or id, like \"Music\" or \"Game\".
        mix: Optional. Only in this mix, like \"Stream Mix\". Leave it empty for both mixes.
        state: Optional. \"Bypassed\", \"NotBypassed\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true when the filters are skipped, false when they work.
        Same as cb.call(\"wavelink.setFilterBypass\", ...).
        """
    def wavelink_set_input_clip_guard(self, device: Any, *, state: Any = None) -> Any:
        """Turns Clipguard (it stops your mic from distorting when you get loud) of a Wave Link input on or off. Fails with an error when Wave Link is not connected.

        device: The input device or microphone, by name or id, like \"Wave:3\".
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"wavelink.setInputClipGuard\", ...).
        """
    def wavelink_set_input_gain(self, device: Any, gain: Any, *, relative: Any = None) -> Any:
        """Changes the gain (how loud the mic picks you up) of a Wave Link input. Fails with an error when Wave Link is not connected.

        device: The input device or microphone, by name or id, like \"Wave:3\".
        gain: The gain. Wave Link 3: 0-100 percent. Wave Link 1.x/2.x: the microphone's own gain number.
        relative: Optional. true adds the number to the current value instead (use a minus number to go down). Default false.
        Gives back: The new gain.
        Same as cb.call(\"wavelink.setInputGain\", ...).
        """
    def wavelink_set_input_low_cut(self, device: Any, *, state: Any = None) -> Any:
        """Turns the low cut filter (it removes deep rumble) of a Wave Link input on or off. Fails with an error when Wave Link is not connected.

        device: The input device or microphone, by name or id, like \"Wave:3\".
        state: Optional. \"On\", \"Off\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true for on, false for off.
        Same as cb.call(\"wavelink.setInputLowCut\", ...).
        """
    def wavelink_set_input_mute(self, device: Any, *, state: Any = None) -> Any:
        """Mutes or unmutes a Wave Link input device, like your microphone. Fails with an error when Wave Link is not connected.

        device: The input device or microphone, by name or id, like \"Wave:3\".
        state: Optional. \"Muted\", \"NotMuted\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true when muted, false when not.
        Same as cb.call(\"wavelink.setInputMute\", ...).
        """
    def wavelink_set_main_output(self, device: Any) -> Any:
        """Picks the main output device in Wave Link: where you listen. Fails with an error when Wave Link is not connected.

        device: The output device, by name or id, like \"Headphones\".
        Gives back: The name of the new main output.
        Same as cb.call(\"wavelink.setMainOutput\", ...).
        """
    def wavelink_set_mic_pc_balance(self, device: Any, balance: Any, *, relative: Any = None) -> Any:
        """Changes the Mic/PC balance of a Wave Link input: how much of your mic versus your computer sound you hear. Fails with an error when Wave Link is not connected.

        device: The input device or microphone, by name or id, like \"Wave:3\".
        balance: The balance, 0-100, like 50.
        relative: Optional. true adds the number to the current value instead (use a minus number to go down). Default false.
        Gives back: The new balance.
        Same as cb.call(\"wavelink.setMicPcBalance\", ...).
        """
    def wavelink_set_mix_mute(self, mix: Any, *, state: Any = None) -> Any:
        """Mutes or unmutes a whole Wave Link mix. Fails with an error when Wave Link is not connected.

        mix: The mix, by name or id, like \"Stream Mix\" or \"Monitor Mix\".
        state: Optional. \"Muted\", \"NotMuted\" or \"Toggle\" (switch). Default \"Toggle\".
        Gives back: The new state: true when muted, false when not.
        Same as cb.call(\"wavelink.setMixMute\", ...).
        """
    def wavelink_set_mix_volume(self, mix: Any, volume: Any, *, relative: Any = None) -> Any:
        """Changes the volume of a whole Wave Link mix. Fails with an error when Wave Link is not connected.

        mix: The mix, by name or id, like \"Stream Mix\" or \"Monitor Mix\".
        volume: The volume in percent, 0-100, like 80.
        relative: Optional. true adds the number to the current value instead (use a minus number to go down). Default false.
        Gives back: The new volume.
        Same as cb.call(\"wavelink.setMixVolume\", ...).
        """
    def wavelink_set_monitored_mix(self, mix: Any) -> Any:
        """Picks which Wave Link mix you hear on your main output. Fails with an error when Wave Link is not connected.

        mix: The mix, by name or id, like \"Stream Mix\" or \"Monitor Mix\".
        Gives back: The name of the mix you hear now.
        Same as cb.call(\"wavelink.setMonitoredMix\", ...).
        """
    def wavelink_set_output_volume(self, device: Any, volume: Any, *, relative: Any = None) -> Any:
        """Changes the volume of a Wave Link output device. Only for Wave Link 3. Fails with an error when Wave Link is not connected.

        device: The output device, by name or id, like \"Headphones\".
        volume: The volume in percent, 0-100, like 70.
        relative: Optional. true adds the number to the current value instead (use a minus number to go down). Default false.
        Gives back: The new volume.
        Same as cb.call(\"wavelink.setOutputVolume\", ...).
        """
    def wavelink_switch_monitoring(self) -> Any:
        """Switches what you hear between Monitor Mix and Stream Mix. Only for Wave Link 1.x/2.x; Wave Link 3 fails with an error (use wavelink.setMonitoredMix). Fails with an error when Wave Link is not connected.

        Gives back: The name of the mix you hear now.
        Same as cb.call(\"wavelink.switchMonitoring\", ...).
        """
    def webhooks_send_test_event(self, service: Any, *, kind: Any = None) -> Any:
        """Pretends a webhook service sent an event, like a Ko-fi donation, so you can test your triggers and alerts. Nothing is sent to the service.

        service: The service: \"kofi\", \"patreon\", \"fourthwall\", \"shopify\", \"throne\" or \"custom\".
        kind: Optional. Which test event, like \"donation\" for Ko-fi. webhooks.status lists them as testKinds. Leave it out for the first one.
        Gives back: true when a test event was sent, false when the service or kind is unknown.
        Same as cb.call(\"webhooks.sendTestEvent\", ...).
        """
    def webhooks_status(self) -> Any:
        """Shows each webhook service (Ko-fi, Patreon and so on): whether it is set up, turned on, and how the last delivery went.

        Gives back: A list. Each service has service, name, configured, enabled, lastDelivery (a time), lastOutcome and testKinds.
        Same as cb.call(\"webhooks.status\", ...).
        """
    def websocket_broadcast(self, data: Any, *, json: Any = None) -> Any:
        """Sends data to every app connected to Cuebot's WebSocket server, as a General.Custom event. Handy for your own overlays and tools.

        data: What to send, like \"{\\\"alert\\\": \\\"raid\\\"}\" or plain text.
        json: Optional. true reads data as JSON and sends it as an object (plain text if it is not valid JSON). Leave it out for false (send as text).
        Gives back: How many apps got it.
        Same as cb.call(\"websocket.broadcast\", ...).
        """
    def websocket_client_connect(self, connection: Any) -> Any:
        """Connects one of your WebSocket clients (set up on the Servers page). Does nothing when it is already connected. Fails when no client matches.

        connection: Which WebSocket client from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: Nothing.
        Same as cb.call(\"websocket.clientConnect\", ...).
        """
    def websocket_client_disconnect(self, connection: Any) -> Any:
        """Disconnects one of your WebSocket clients. Fails when no client matches.

        connection: Which WebSocket client from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: Nothing.
        Same as cb.call(\"websocket.clientDisconnect\", ...).
        """
    def websocket_client_index(self, name: Any) -> Any:
        """Finds the number of a WebSocket client in the list on the Servers page, by its name.

        name: The client's name, like \"My overlay\".
        Gives back: Its number (0 is the first), or -1 when no client has that name.
        Same as cb.call(\"websocket.clientIndex\", ...).
        """
    def websocket_client_is_connected(self, connection: Any) -> Any:
        """Checks whether one of your WebSocket clients is connected right now. Fails when no client matches.

        connection: Which WebSocket client from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: true when it is connected, otherwise false.
        Same as cb.call(\"websocket.clientIsConnected\", ...).
        """
    def websocket_client_send(self, connection: Any, data: Any) -> Any:
        """Sends a message through one of your WebSocket clients. Fails when no client matches.

        connection: Which WebSocket client from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        data: What to send, like \"hello\".
        Gives back: true when it was sent, false when the client is not connected.
        Same as cb.call(\"websocket.clientSend\", ...).
        """
    def websocket_server_broadcast(self, connection: Any, data: Any, *, session_id: Any = None) -> Any:
        """Sends a message to the apps connected to one of your custom WebSocket servers. Fails when the server is not running.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        data: What to send, like \"hello\".
        session_id: Optional. Send only to this one connection (its session id). Leave it out for everyone.
        Gives back: How many connections got it.
        Same as cb.call(\"websocket.serverBroadcast\", ...).
        """
    def websocket_server_close_all(self, connection: Any) -> Any:
        """Disconnects every app connected to one of your custom WebSocket servers. The server keeps running.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: How many connections were closed (0 when the server is not running).
        Same as cb.call(\"websocket.serverCloseAll\", ...).
        """
    def websocket_server_close_session(self, connection: Any, session_id: Any) -> Any:
        """Disconnects one app from one of your custom WebSocket servers.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        session_id: The session id of the connection to close, like the %sessionId% of a custom server trigger.
        Gives back: true when it was closed, false when there is no such connection or the server is not running.
        Same as cb.call(\"websocket.serverCloseSession\", ...).
        """
    def websocket_server_index(self, name: Any) -> Any:
        """Finds the number of a custom WebSocket server in the list on the Servers page, by its name.

        name: The server's name, like \"Game server\".
        Gives back: Its number (0 is the first), or -1 when no server has that name.
        Same as cb.call(\"websocket.serverIndex\", ...).
        """
    def websocket_server_is_listening(self, connection: Any) -> Any:
        """Checks whether one of your custom WebSocket servers is running.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: true when it is running, otherwise false.
        Same as cb.call(\"websocket.serverIsListening\", ...).
        """
    def websocket_server_start(self, connection: Any) -> Any:
        """Starts one of your custom WebSocket servers, so other apps can connect. Fails when no server matches.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: Nothing.
        Same as cb.call(\"websocket.serverStart\", ...).
        """
    def websocket_server_stop(self, connection: Any) -> Any:
        """Stops one of your custom WebSocket servers. Connected apps are disconnected.

        connection: Which custom WebSocket server from the Servers page: its number in the list (0 is the first), its name or its id. Leave it out for 0.
        Gives back: Nothing.
        Same as cb.call(\"websocket.serverStop\", ...).
        """
    def youtube_add_moderator(self, user: Any, *, broadcast_id: Any = None) -> Any:
        """Makes a viewer a moderator of your live YouTube chat. Fails when YouTube is not connected or you are not live.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: Nothing.
        Same as cb.call(\"youtube.addModerator\", ...).
        """
    def youtube_add_tags(self, tags: Any, *, broadcast_id: Any = None) -> Any:
        """Adds tags to your YouTube broadcast. Tags it already has are kept, and doubles are skipped.

        tags: The tags to add, as a list like [\"gaming\", \"chill\"] or as text with commas, like \"gaming, chill\".
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: The full list of tags the video has now.
        Same as cb.call(\"youtube.addTags\", ...).
        """
    def youtube_ban_user(self, user: Any, *, broadcast_id: Any = None) -> Any:
        """Bans a viewer from your live YouTube chat for good. Fails when YouTube is not connected or you are not live.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: Nothing.
        Same as cb.call(\"youtube.banUser\", ...).
        """
    def youtube_clear_tags(self, *, broadcast_id: Any = None) -> Any:
        """Removes every tag from your YouTube broadcast.

        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: The list of tags the video has now (empty).
        Same as cb.call(\"youtube.clearTags\", ...).
        """
    def youtube_create_poll(self, question: Any, options: Any, *, broadcast_id: Any = None) -> Any:
        """Starts a poll in your live YouTube chat. It needs a question and 2 to 4 options.

        question: The question, like \"Which game next?\"
        options: The answers, as a list like [\"Minecraft\", \"Fortnite\"] or as text with commas.
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: The id of the poll message. You can give it to youtube.endPoll.
        Same as cb.call(\"youtube.createPoll\", ...).
        """
    def youtube_delete_message(self, message_id: Any) -> Any:
        """Deletes one message from your YouTube chat. Fails when YouTube is not connected or the id is empty.

        message_id: The id of the chat message, like the %messageId% of a chat trigger.
        Gives back: Nothing.
        Same as cb.call(\"youtube.deleteMessage\", ...).
        """
    def youtube_end_poll(self, *, poll_message_id: Any = None) -> Any:
        """Ends a YouTube chat poll. Fails when there is no active poll.

        poll_message_id: Optional. The poll id from youtube.createPoll. Leave it out for the poll Cuebot started last.
        Gives back: Nothing.
        Same as cb.call(\"youtube.endPoll\", ...).
        """
    def youtube_get_bot(self) -> Any:
        """Gives you the details of your YouTube bot account, as set on the YouTube page.

        Gives back: An object with userName, userId, customUrl, profileImageUrl and isSignedIn.
        Same as cb.call(\"youtube.getBot\", ...).
        """
    def youtube_get_broadcaster(self) -> Any:
        """Gives you the details of your own YouTube account, as set on the YouTube page.

        Gives back: An object with userName, userId, customUrl, profileImageUrl and isSignedIn.
        Same as cb.call(\"youtube.getBroadcaster\", ...).
        """
    def youtube_get_channel_info(self, user: Any) -> Any:
        """Looks up a YouTube channel. Fails when you are not signed in or the channel cannot be found.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        Gives back: An object with id, title, description, customUrl, url, profileImageUrl, subscriberCount, subscriberCountHidden, videoCount, viewCount and createdAt.
        Same as cb.call(\"youtube.getChannelInfo\", ...).
        """
    def youtube_get_latest_monitored_broadcast(self) -> Any:
        """Gives you the newest live broadcast Cuebot is watching.

        Gives back: The broadcast with id, title, description, privacy, status, liveChatId, url, actualStart, actualEnd, scheduledStart, channelId and thumbnailUrl. null when none is being watched.
        Same as cb.call(\"youtube.getLatestMonitoredBroadcast\", ...).
        """
    def youtube_get_monitored_broadcasts(self) -> Any:
        """Lists every live broadcast Cuebot is watching right now.

        Gives back: A list of broadcasts, each with id, title, description, privacy, status, liveChatId, url, actualStart, actualEnd, scheduledStart, channelId and thumbnailUrl. Empty when there are none.
        Same as cb.call(\"youtube.getMonitoredBroadcasts\", ...).
        """
    def youtube_get_quota(self) -> Any:
        """Shows how much of your daily YouTube allowance (quota) Cuebot has used. YouTube limits how many requests you can make per day.

        Gives back: An object with used (units used today, an estimate), dailyQuota, exhausted (true when none is left) and resetsAt (when it starts over).
        Same as cb.call(\"youtube.getQuota\", ...).
        """
    def youtube_get_video_info(self, *, video_id: Any = None) -> Any:
        """Looks up a YouTube video, with live numbers like viewers and likes. Fails when you are not signed in or the video cannot be found.

        video_id: Optional. The video id, like \"dQw4w9WgXcQ\". Leave it out for your live broadcast (or the next upcoming one).
        Gives back: An object with id, title, description, url, channelId, channelTitle, categoryId, tags, privacy, liveStatus, thumbnail, concurrentViewers, viewCount, likeCount, commentCount, actualStart and actualEnd.
        Same as cb.call(\"youtube.getVideoInfo\", ...).
        """
    def youtube_remove_moderator(self, user: Any, *, broadcast_id: Any = None) -> Any:
        """Takes moderator away from a viewer in your live YouTube chat. Fails when they are not a moderator.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: Nothing.
        Same as cb.call(\"youtube.removeModerator\", ...).
        """
    def youtube_remove_tags(self, tags: Any, *, broadcast_id: Any = None) -> Any:
        """Removes some tags from your YouTube broadcast. Upper or lower case does not matter.

        tags: The tags to remove, as a list like [\"chill\"] or as text with commas.
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: The full list of tags the video has now.
        Same as cb.call(\"youtube.removeTags\", ...).
        """
    def youtube_send_message(self, message: Any, *, use_bot: Any = None, fallback: Any = None, broadcast_id: Any = None) -> Any:
        """Sends a message in your live YouTube chat. Text over 200 characters is split. Fails when YouTube is not connected or you are not live.

        message: What to say, like \"Welcome to the stream!\"
        use_bot: Optional. true sends it with your bot account. Leave it out to send as yourself.
        fallback: Optional. With useBot, true (the default) sends as yourself when the bot is not signed in or fails; false makes it fail instead.
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: Nothing.
        Same as cb.call(\"youtube.sendMessage\", ...).
        """
    def youtube_send_message_to_latest_monitored(self, message: Any, *, use_bot: Any = None, fallback: Any = None) -> Any:
        """Sends a message in the chat of your newest live YouTube broadcast. Text over 200 characters is split. Fails when you are not live.

        message: What to say, like \"Welcome to the stream!\"
        use_bot: Optional. true sends it with your bot account. Leave it out to send as yourself.
        fallback: Optional. With useBot, true (the default) sends as yourself when the bot is not signed in or fails; false makes it fail instead.
        Gives back: Nothing.
        Same as cb.call(\"youtube.sendMessageToLatestMonitored\", ...).
        """
    def youtube_set_category(self, category_id: Any, *, broadcast_id: Any = None) -> Any:
        """Changes the category of your YouTube broadcast. YouTube uses number ids for categories, like \"20\" for Gaming.

        category_id: The category id, like \"20\" (Gaming) or \"22\" (People & Blogs).
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: Nothing.
        Same as cb.call(\"youtube.setCategory\", ...).
        """
    def youtube_set_description(self, description: Any, *, broadcast_id: Any = None) -> Any:
        """Changes the description of your YouTube broadcast.

        description: The new description, like \"Playing with chat tonight!\"
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: Nothing.
        Same as cb.call(\"youtube.setDescription\", ...).
        """
    def youtube_set_metadata(self, *, title: Any = None, description: Any = None, broadcast_id: Any = None) -> Any:
        """Changes the title, the description, or both, of your YouTube broadcast in one go. What you leave out stays as it is.

        title: Optional. The new title. Leave it out (or empty) to keep it.
        description: Optional. The new description. Leave it out to keep it.
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: Nothing.
        Same as cb.call(\"youtube.setMetadata\", ...).
        """
    def youtube_set_privacy(self, privacy: Any, *, broadcast_id: Any = None) -> Any:
        """Changes who can see your YouTube broadcast.

        privacy: \"public\" (everyone), \"unlisted\" (only people with the link) or \"private\" (only you).
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: Nothing.
        Same as cb.call(\"youtube.setPrivacy\", ...).
        """
    def youtube_set_title(self, title: Any, *, broadcast_id: Any = None) -> Any:
        """Changes the title of your YouTube broadcast. Fails when you are not signed in or there is no live or upcoming broadcast.

        title: The new title, like \"Chill games with chat\".
        broadcast_id: Optional. The video id of the broadcast to change. Leave it out for your live broadcast (or the next upcoming one).
        Gives back: Nothing.
        Same as cb.call(\"youtube.setTitle\", ...).
        """
    def youtube_timeout_user(self, user: Any, *, duration: Any = None, broadcast_id: Any = None) -> Any:
        """Times out a viewer in your live YouTube chat for some seconds. Fails when YouTube is not connected or you are not live.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        duration: Optional. How long, in seconds, like 300. Leave it out for 300 (5 minutes).
        broadcast_id: Optional. The id of a monitored broadcast. Leave it out for your latest live broadcast.
        Gives back: Nothing.
        Same as cb.call(\"youtube.timeoutUser\", ...).
        """
    def youtube_unban_user(self, user: Any) -> Any:
        """Lifts a ban or timeout. YouTube only lets Cuebot undo bans it placed itself since it started; use YouTube Studio for older ones.

        user: The viewer: their channel id (starts with UC), their @handle, or the name of someone who chatted recently.
        Gives back: Nothing. Fails when Cuebot did not ban this viewer in this session.
        Same as cb.call(\"youtube.unbanUser\", ...).
        """


cb: Cuebot
