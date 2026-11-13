"""
Cuebot for StreamController: keys and dials that run Cuebot actions, toggle them, set global variables and fire
Cuebot's Stream Deck Action trigger, and show what Cuebot's Stream Deck sub-actions send them.

It speaks the same protocol as Cuebot's Elgato plugin (src/Cuebot.StreamDeck/PROTOCOL.md), so Cuebot treats it like a
Stream Deck app: it is listed on Cuebot's Stream Deck page and the Set Title / Set State / Set Background / Set Value /
Show Alert / Show Ok / Toggle State sub-actions reach its keys by button id.
"""

import io
import os
import sys
import threading

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, GLib, Gtk  # noqa: E402

from loguru import logger as log  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

from src.backend.DeckManagement.InputIdentifier import Input  # noqa: E402
from src.backend.PluginManager.ActionBase import ActionBase  # noqa: E402
from src.backend.PluginManager.ActionHolder import ActionHolder  # noqa: E402
from src.backend.PluginManager.ActionInputSupport import ActionInputSupport  # noqa: E402
from src.backend.PluginManager.PluginBase import PluginBase  # noqa: E402

import globals as gl  # noqa: E402

sys.path.append(os.path.dirname(__file__))

from cuebot_core import (  # noqa: E402
    PLUGIN_UUID,
    ButtonInfo,
    CuebotClient,
    KeyRegistry,
    RequestError,
    action_choices,
    connection_from,
    decode_data_uri,
    device_type_number,
    fetch_image_as_data_uri,
    find_action,
    new_button_id,
    parse_args,
    value_text,
)

PLUGIN_VERSION = "1.0.0"


def _asset(name: str) -> str:
    return os.path.join(os.path.dirname(__file__), "assets", name)


def _ok_image() -> Image.Image:
    """A green check mark for Show Ok (StreamController only has an error overlay built in)."""
    size = 144
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((22, 22, size - 22, size - 22), fill=(46, 160, 67, 235))
    d.line([(46, 74), (64, 92), (100, 54)], fill=(255, 255, 255, 255), width=12, joint="curve")
    return img


class CuebotPlugin(PluginBase):
    def __init__(self):
        super().__init__()
        self.actions_cache: list[dict] | None = None
        self.registry = KeyRegistry(load_image=fetch_image_as_data_uri, on_error=lambda m: log.warning(f"Cuebot: {m}"))
        self.client = CuebotClient(hello=self._hello)
        self.client.command_listeners.append(self.registry.apply)
        self.client.actions_listeners.append(self._on_actions_changed)
        self.client.global_listeners.append(self._on_global_changed)
        self.client.status_listeners.append(self._on_status)
        self._status_rows: list = []
        self._reported_devices: list | None = None
        self._seen_controllers: list = []
        self.ok_image = _ok_image()
        self.has_plugin_settings = True

        holders = [
            ("run", RunActionKey, "Run Cuebot action", {Input.Key: ActionInputSupport.SUPPORTED,
                                                         Input.Dial: ActionInputSupport.UNSUPPORTED,
                                                         Input.Touchscreen: ActionInputSupport.UNSUPPORTED}),
            ("toggle", ToggleActionKey, "Toggle action enabled", {Input.Key: ActionInputSupport.SUPPORTED,
                                                                  Input.Dial: ActionInputSupport.UNSUPPORTED,
                                                                  Input.Touchscreen: ActionInputSupport.UNSUPPORTED}),
            ("global", SetGlobalKey, "Set global variable", {Input.Key: ActionInputSupport.SUPPORTED,
                                                             Input.Dial: ActionInputSupport.SUPPORTED,
                                                             Input.Touchscreen: ActionInputSupport.UNSUPPORTED}),
            ("trigger", TriggerKey, "Cuebot trigger", {Input.Key: ActionInputSupport.SUPPORTED,
                                                       Input.Dial: ActionInputSupport.SUPPORTED,
                                                       Input.Touchscreen: ActionInputSupport.UNSUPPORTED}),
        ]
        for kind, base, name, support in holders:
            self.add_action_holder(ActionHolder(
                plugin_base=self,
                action_base=base,
                action_id_suffix=kind.capitalize(),
                action_name=name,
                icon=Gtk.Picture.new_for_filename(_asset(f"action-{kind}.png")),
                action_support=support,
            ))

        self.register(
            plugin_name="Cuebot",
            github_repo="https://github.com/TechDaddyKB/cuebot",
            plugin_version=PLUGIN_VERSION,
            app_version="1.5.0-beta",
        )
        self.client.configure(connection_from(self.get_settings()))

    # -- connection --

    def _hello(self) -> dict:
        self._reported_devices = self.devices()
        return {
            "plugin": {"uuid": PLUGIN_UUID, "version": PLUGIN_VERSION},
            "app": {"name": "StreamController", "version": str(getattr(gl, "app_version", "")),
                    "platform": "linux", "language": "en"},
            "devices": self._reported_devices,
            "buttons": self.registry.list(),
        }

    def devices(self) -> list[dict]:
        result = []
        manager = getattr(gl, "deck_manager", None)
        controllers = list(getattr(manager, "deck_controller", []) or []) + list(self._seen_controllers)
        seen = set()
        for controller in controllers:
            if id(controller) in seen:
                continue
            seen.add(id(controller))
            try:
                deck = controller.deck
                rows, columns = deck.key_layout()
                kind = deck.deck_type()
                result.append({
                    "id": controller.safe_serial_number(),
                    "name": kind,
                    "type": device_type_number(kind),
                    "size": {"columns": columns, "rows": rows},
                    "connected": True,
                })
            except Exception as e:  # noqa: BLE001
                log.debug(f"Cuebot: skipping a deck: {e}")
        return result

    def report_devices(self, controller=None) -> None:
        """Tells Cuebot about the decks when they change (they load after the hello, and keys can load first)."""
        if controller is not None and controller not in self._seen_controllers:
            self._seen_controllers.append(controller)
        devices = self.devices()
        if devices != self._reported_devices and self.client.send({"type": "devices", "devices": devices}):
            self._reported_devices = devices

    def connection_settings(self) -> dict:
        s = self.get_settings()
        return {"host": s.get("host", ""), "port": s.get("port", ""), "token": s.get("token", "")}

    def save_connection(self, **changes) -> None:
        s = self.get_settings()
        s.update(changes)
        self.set_settings(s)
        self.client.configure(connection_from(s))

    def _on_status(self, status) -> None:
        log.info(f"Cuebot: {status.state} - {status.message}")
        GLib.idle_add(self._show_status)
        if status.state == "connected":
            threading.Thread(target=self._refresh_after_connect, daemon=True, name="cuebot-refresh").start()

    def _show_status(self) -> bool:
        text = self.client.status.message
        for row in list(self._status_rows):
            try:
                row.set_subtitle(GLib.markup_escape_text(text))
            except Exception:
                self._status_rows.remove(row)
        return False

    def add_status_row(self, row) -> None:
        self._status_rows.append(row)
        row.set_subtitle(GLib.markup_escape_text(self.client.status.message))

    def _refresh_after_connect(self) -> None:
        try:
            result = self.client.request("getActions")
            self._on_actions_changed(result.get("actions") or [])
        except RequestError:
            pass
        for key in list(CuebotKey.instances):
            key.after_connect()

    def _on_actions_changed(self, actions: list) -> None:
        self.actions_cache = list(actions)
        for key in list(CuebotKey.instances):
            key.on_actions(self.actions_cache)

    def _on_global_changed(self, name: str, value) -> None:
        for key in list(CuebotKey.instances):
            key.on_global(name, value)

    def fetch_actions(self, done) -> None:
        """Loads the action list off the UI thread and calls done(actions) on the UI thread."""
        def work():
            try:
                result = self.client.request("getActions")
                self.actions_cache = list(result.get("actions") or [])
            except RequestError as e:
                log.debug(f"Cuebot: action list not loaded: {e}")
            GLib.idle_add(lambda: (done(self.actions_cache or []), False)[1])
        threading.Thread(target=work, daemon=True, name="cuebot-actions").start()

    # -- settings window --

    def get_settings_area(self):
        group = Adw.PreferencesGroup(
            title="Connection to Cuebot",
            description="In Cuebot, open Input > Stream Deck, click Turn on the REST API and copy the token. "
                        "Host and port only change when Cuebot runs on another computer.")
        s = self.connection_settings()
        host = Adw.EntryRow(title="Host (default 127.0.0.1)")
        host.set_text(str(s["host"] or ""))
        port = Adw.EntryRow(title="Port (default 7475)")
        port.set_text(str(s["port"] or ""))
        token = Adw.PasswordEntryRow(title="API token")
        token.set_text(str(s["token"] or ""))
        status = Adw.ActionRow(title="Status")
        button = Gtk.Button(label="Reconnect", valign=Gtk.Align.CENTER)
        button.connect("clicked", lambda *_: self.client.reconnect())
        status.add_suffix(button)
        self.add_status_row(status)
        host.connect("notify::text", lambda e, *_: self.save_connection(host=e.get_text().strip()))
        port.connect("notify::text", lambda e, *_: self.save_connection(port=e.get_text().strip()))
        token.connect("notify::text", lambda e, *_: self.save_connection(token=e.get_text().strip()))
        for row in (host, port, token, status):
            group.add(row)
        return group

    def get_selector_icon(self) -> Gtk.Widget:
        return Gtk.Image.new_from_file(_asset("icon.png"))

    def on_uninstall(self) -> None:
        self.client.stop()
        super().on_uninstall()


class CuebotKey(ActionBase):
    """
    Shared behaviour of every Cuebot key and dial: a button id (assigned on first use) so Cuebot's sub-actions can reach
    it, registration with Cuebot while loaded, and drawing what Cuebot sends (titles and images per state, values).
    """

    kind = "trigger"
    instances: "set[CuebotKey]" = set()

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.has_configuration = True
        self.look_state = 0
        self.titles: dict = {}
        self.images: dict = {}
        self.value: str | None = None
        self.indicator: int | None = None
        self._image_cache: dict = {}
        self._context: str | None = None

    # -- KeyTarget for the registry (called from the connection thread) --

    @property
    def is_dial(self) -> bool:
        return isinstance(self.input_ident, Input.Dial)

    def set_title(self, title, state=None):
        if state is None:
            self.titles = {"*": title}
        else:
            self.titles.pop("*", None)
            self.titles[state] = title
        self.render()

    def set_state(self, state):
        self.look_state = 1 if state == 1 else 0
        self.render()

    def set_image(self, image, state=None):
        if state is None:
            self.images = {"*": image}
        else:
            self.images.pop("*", None)
            self.images[state] = image
        self.render()

    def set_value(self, value, indicator):
        self.value = value
        self.indicator = indicator
        if not self.is_dial:
            self.titles = {"*": value or None}
        self.render()

    def show_alert(self):
        self._safe(lambda: self.show_error(duration=1))

    def show_ok(self):
        self._safe(lambda: self.show_overlay(self.plugin_base.ok_image, duration=1))

    # -- lifecycle --

    def on_ready(self):
        CuebotKey.instances.add(self)
        self._context = self._make_context()
        settings = self.ensure_button_id()
        info = self.button_info(settings)
        self.plugin_base.registry.add(info, self)
        self.plugin_base.report_devices(self.deck_controller)
        self.plugin_base.client.send({"type": "appear", "button": info.to_json()})
        self.render()
        self.refresh()

    def on_remove(self):
        self._forget()

    def on_removed_from_cache(self):
        self._forget()
        super().on_removed_from_cache()

    def _forget(self):
        CuebotKey.instances.discard(self)
        if self._context and self.plugin_base.registry.remove(self._context):
            self.plugin_base.client.send({"type": "disappear", "context": self._context})

    def context(self) -> str:
        """Stable while the key is loaded (computed when it appears)."""
        return self._context or self._make_context()

    def _make_context(self) -> str:
        page = self.page.get_name() if self.page else ""
        index = 0
        try:
            index = max(0, self.get_own_action_index() or 0)
        except Exception:
            pass
        return f"{self.deck_serial()}/{page}/{self.input_ident.input_type}/{self.input_ident.json_identifier}/{self.state}/{index}"

    def deck_serial(self) -> str:
        try:
            return self.deck_controller.safe_serial_number()
        except Exception:
            return "unknown"

    def ensure_button_id(self) -> dict:
        settings = self.get_settings()
        if not str(settings.get("buttonId") or "").strip():
            settings["buttonId"] = new_button_id(self.kind)
            self.set_settings(settings)
        return settings

    def button_info(self, settings: dict) -> ButtonInfo:
        row = column = None
        if isinstance(self.input_ident, Input.Key):
            column, row = self.input_ident.coords
        return ButtonInfo(
            context=self.context(),
            button_id=str(settings.get("buttonId") or ""),
            kind=self.kind,
            controller="Encoder" if self.is_dial else "Keypad",
            device=self.deck_serial(),
            row=row,
            column=column,
            state=self.look_state,
            is_in_multi_action=self.get_is_multi_action(),
        )

    # -- hooks for kinds --

    def refresh(self):
        """Kind-specific update after appearing, a settings change and a reconnect."""

    def after_connect(self):
        self.plugin_base.client.send({"type": "appear", "button": self.button_info(self.get_settings()).to_json()})
        self.refresh()

    def on_actions(self, actions: list):
        pass

    def on_global(self, name: str, value):
        pass

    def default_image(self) -> str:
        return _asset(("dial-" if self.is_dial and self.kind in ("global", "trigger") else "") + f"{self.kind}.png")

    def default_title(self) -> str | None:
        return None

    # -- drawing --

    def render(self):
        def draw():
            image = self.images.get(self.look_state, self.images.get("*"))
            picture = self._picture(image) if image else None
            if picture is not None:
                self.set_media(image=picture, size=1.0)
            else:
                self.set_media(media_path=self.default_image(), size=1.0)
            title = self.titles.get(self.look_state, self.titles.get("*"))
            if title is None:
                title = self.value if self.is_dial and self.value is not None else self.default_title()
            self.set_bottom_label(title or "", font_size=12)
        self._safe(draw)

    def _picture(self, uri: str):
        if uri in self._image_cache:
            return self._image_cache[uri]
        try:
            picture = Image.open(io.BytesIO(decode_data_uri(uri))).convert("RGBA")
        except Exception as e:  # noqa: BLE001
            log.warning(f"Cuebot: could not show an image: {e}")
            picture = None
        self._image_cache = {uri: picture}
        return picture

    def _safe(self, fn):
        try:
            if self.on_ready_called:
                fn()
        except Exception as e:  # noqa: BLE001 - the key went away between the lookup and the call
            log.debug(f"Cuebot: key update skipped: {e}")

    def outcome(self, work):
        """Runs a request; OK on success, the error overlay (and a log line) on failure."""
        try:
            result = work()
            if not self.is_dial:
                self.show_ok()
            return result
        except Exception as e:  # noqa: BLE001
            log.warning(f"Cuebot request failed: {e}")
            self.show_alert()
            return None

    # -- settings UI --

    def setting(self, name, default=None):
        return self.get_settings().get(name, default)

    def update_setting(self, name, value, refresh=True):
        settings = self.get_settings()
        settings[name] = value
        self.set_settings(settings)
        if name == "buttonId":
            self.plugin_base.registry.update_button_id(self.context(), value)
            self.plugin_base.client.send({"type": "appear", "button": self.button_info(settings).to_json()})
        if refresh:
            self.render()
            self.refresh()

    def status_row(self):
        row = Adw.ActionRow(title="Cuebot")
        self.plugin_base.add_status_row(row)
        return row

    def entry_row(self, title, name, default=""):
        row = Adw.EntryRow(title=title)
        row.set_text(str(self.setting(name, default) or ""))
        row.connect("notify::text", lambda e, *_: self.update_setting(name, e.get_text()))
        return row

    def switch_row(self, title, name, default, subtitle=None):
        row = Adw.SwitchRow(title=title)
        if subtitle:
            row.set_subtitle(subtitle)
        row.set_active(bool(self.setting(name, default)))
        row.connect("notify::active", lambda r, *_: self.update_setting(name, r.get_active()))
        return row

    def choice_row(self, title, name, options: list[tuple[str, str]], default):
        row = Adw.ComboRow(title=title)
        row.set_model(Gtk.StringList.new([label for _, label in options]))
        values = [v for v, _ in options]
        current = self.setting(name, default)
        row.set_selected(values.index(current) if current in values else values.index(default))
        row.connect("notify::selected", lambda r, *_: self.update_setting(name, values[r.get_selected()]))
        return row

    def action_row(self):
        """A combo of Cuebot's actions; stores actionId and actionName."""
        row = Adw.ComboRow(title="Action")
        state = {"ids": [], "loading": True}

        def fill(actions):
            choices = action_choices(actions)
            name = self.setting("actionName")
            found = find_action(actions, self.setting("actionId"), name)
            current = found.get("id") if found else self.setting("actionId")
            ids = [c[0] for c in choices]
            labels = [c[1] + ("" if c[2] else " (disabled)") for c in choices]
            if (current or name) and current not in ids:
                # Kept, so opening the settings never changes what the key runs.
                ids.insert(0, current or "")
                labels.insert(0, f"{name or current} (not in Cuebot)" if actions else (name or current))
            elif not current:
                ids.insert(0, "")
                labels.insert(0, "Pick an action" if actions else "Connect to Cuebot to pick an action")
            state["loading"] = True
            state["ids"] = ids
            row.set_model(Gtk.StringList.new(labels))
            row.set_selected(ids.index(current or ""))
            state["loading"] = False

        def changed(r, *_):
            if state["loading"]:
                return
            index = r.get_selected()
            if index == Gtk.INVALID_LIST_POSITION or index >= len(state["ids"]) or not state["ids"][index]:
                return
            chosen = find_action(self.plugin_base.actions_cache or [], state["ids"][index], None)
            settings = self.get_settings()
            settings["actionId"] = state["ids"][index]
            settings["actionName"] = chosen.get("name") if chosen else settings.get("actionName", "")
            self.set_settings(settings)
            self.render()
            self.refresh()

        fill(self.plugin_base.actions_cache or [])
        row.connect("notify::selected", changed)
        self.plugin_base.fetch_actions(fill)
        return row

    def args_area(self, title="Arguments"):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin_top=12)
        box.append(Gtk.Label(label=f"{title}: one name=value per line (or a JSON object)", xalign=0,
                             css_classes=["dim-label"]))
        view = Gtk.TextView(monospace=True, wrap_mode=Gtk.WrapMode.WORD_CHAR, height_request=72,
                            top_margin=6, bottom_margin=6, left_margin=6, right_margin=6)
        view.get_buffer().set_text(str(self.setting("args", "") or ""))
        view.get_buffer().connect(
            "changed", lambda b: self.update_setting("args", b.get_text(b.get_start_iter(), b.get_end_iter(), False),
                                                     refresh=False))
        frame = Gtk.Frame(child=view)
        box.append(frame)
        return box

    def button_id_row(self):
        row = self.entry_row("Button ID (for Cuebot's Stream Deck sub-actions)", "buttonId")
        return row


class RunActionKey(CuebotKey):
    kind = "run"

    def get_config_rows(self):
        return [self.status_row(), self.action_row(),
                self.choice_row("Run on", "runOn", [("down", "Key down"), ("up", "Key up")], "down"),
                self.button_id_row()]

    def get_custom_config_area(self):
        return self.args_area()

    def default_title(self):
        return self.setting("actionName") or None

    def event_callback(self, event, data=None):
        run_on = self.setting("runOn", "down")
        if (event == Input.Key.Events.DOWN and run_on == "down") or (event == Input.Key.Events.UP and run_on == "up"):
            self.run()

    def run(self):
        settings = self.get_settings()

        def work():
            action = settings.get("actionId") or settings.get("actionName")
            if not action:
                raise RequestError("Pick a Cuebot action in the key's settings")
            args = parse_args(settings.get("args"))
            args["buttonId"] = settings.get("buttonId", "")
            return self.plugin_base.client.request("runAction", {"action": action, "args": args})
        self.outcome(work)


class ToggleActionKey(CuebotKey):
    """Enables, disables or toggles a Cuebot action; state 0 shows enabled, state 1 disabled."""

    kind = "toggle"

    def get_config_rows(self):
        return [self.status_row(), self.action_row(),
                self.choice_row("When pressed", "mode",
                                [("toggle", "Toggle"), ("enable", "Enable"), ("disable", "Disable")], "toggle"),
                self.button_id_row()]

    def default_image(self):
        return _asset("toggle-off.png" if self.look_state == 1 else "toggle-on.png")

    def default_title(self):
        return self.setting("actionName") or None

    def refresh(self):
        if self.plugin_base.actions_cache is not None:
            self.on_actions(self.plugin_base.actions_cache)

    def on_actions(self, actions):
        found = find_action(actions, self.setting("actionId"), self.setting("actionName"))
        if found is None:
            return
        state = 0 if found.get("enabled", True) else 1
        self.plugin_base.registry.set_local_state(self.context(), state)
        if state != self.look_state:
            self.set_state(state)

    def event_callback(self, event, data=None):
        if event != Input.Key.Events.DOWN:
            return
        settings = self.get_settings()

        def work():
            action = settings.get("actionId") or settings.get("actionName")
            if not action:
                raise RequestError("Pick a Cuebot action in the key's settings")
            mode = settings.get("mode", "toggle")
            result = self.plugin_base.client.request(
                "setActionEnabled", {"action": action, "enabled": "toggle" if mode == "toggle" else mode == "enable"})
            self.set_state(0 if (result or {}).get("enabled", True) else 1)
            return result
        self.outcome(work)


class SetGlobalKey(CuebotKey):
    """Sets, adds to or toggles a global variable; on a dial, turning adds the step per tick."""

    kind = "global"

    def get_config_rows(self):
        return [self.status_row(),
                self.entry_row("Global variable", "variable"),
                self.choice_row("Operation", "op", [("set", "Set to the value"), ("add", "Add the value (a number)"),
                                                    ("toggle", "Toggle true / false")], "set"),
                self.entry_row("Value", "value"),
                self.switch_row("Persisted", "persisted", True, "Keep the value after Cuebot restarts"),
                self.entry_row("Dial step per tick", "step", "1"),
                self.switch_row("Show the value", "showValue", False, "Show the variable's current value on the key"),
                self.button_id_row()]

    def default_title(self):
        return self.setting("variable") or None

    def refresh(self):
        if self.setting("showValue") and self.plugin_base.client.is_connected:
            threading.Thread(target=self._load_value, daemon=True, name="cuebot-global").start()

    def _load_value(self):
        try:
            result = self.plugin_base.client.request("getGlobals")
        except RequestError:
            return
        name = str(self.setting("variable", "")).strip().lower()
        for v in (result or {}).get("variables", []):
            if str(v.get("name", "")).lower() == name:
                self.show_value(value_text(v.get("value")))

    def on_global(self, name, value):
        if self.setting("showValue") and str(self.setting("variable", "")).strip().lower() == name.lower():
            self.show_value("" if value is None else value_text(value))

    def show_value(self, text):
        self.value = text
        if not self.is_dial:
            self.titles = {"*": text}
        self.render()

    def _variable(self):
        name = str(self.setting("variable", "")).strip()
        if not name:
            raise RequestError("Enter a global variable name in the key's settings")
        return name

    def event_callback(self, event, data=None):
        settings = self.get_settings()
        client = self.plugin_base.client
        if event in (Input.Key.Events.DOWN, Input.Dial.Events.DOWN):
            self.outcome(lambda: client.request("setGlobal", {
                "name": self._variable(), "value": settings.get("value", ""), "op": settings.get("op", "set"),
                "persisted": settings.get("persisted", True)}))
        elif event in (Input.Dial.Events.TURN_CW, Input.Dial.Events.TURN_CCW):
            try:
                step = float(str(settings.get("step") or "1").strip() or "1")
            except ValueError:
                step = 1.0
            ticks = 1 if event == Input.Dial.Events.TURN_CW else -1
            amount = step * ticks
            if amount == int(amount):
                amount = int(amount)
            self.outcome(lambda: client.request("setGlobal", {
                "name": self._variable(), "value": amount, "op": "add", "persisted": settings.get("persisted", True)}))


class TriggerKey(CuebotKey):
    """Fires Cuebot's Stream Deck Action trigger with the key's button id, the event and its arguments."""

    kind = "trigger"

    def get_config_rows(self):
        return [self.status_row(), self.button_id_row()]

    def get_custom_config_area(self):
        return self.args_area()

    def event_callback(self, event, data=None):
        mapping = {
            Input.Key.Events.DOWN: ("keyDown", {"state": self.look_state}),
            Input.Key.Events.UP: ("keyUp", {"state": self.look_state}),
            Input.Dial.Events.DOWN: ("dialDown", {}),
            Input.Dial.Events.UP: ("dialUp", {}),
            Input.Dial.Events.TURN_CW: ("dialRotate", {"ticks": 1, "pressed": False}),
            Input.Dial.Events.TURN_CCW: ("dialRotate", {"ticks": -1, "pressed": False}),
            Input.Dial.Events.SHORT_TOUCH_PRESS: ("touchTap", {"hold": False}),
            Input.Dial.Events.LONG_TOUCH_PRESS: ("touchTap", {"hold": True}),
        }
        if event not in mapping:
            return
        name, extra = mapping[event]
        if name == "touchTap" and data:
            extra = {**extra, "tapPos": [int(data.get("x", 0)), int(data.get("y", 0))]}
        settings = self.get_settings()
        sent = self.plugin_base.client.send_input(
            event=name, context=self.context(), buttonId=settings.get("buttonId", ""),
            controller="Encoder" if self.is_dial else "Keypad", device=self.deck_serial(),
            args=parse_args(settings.get("args")), **extra)
        if not sent and name in ("keyDown", "dialDown", "touchTap"):
            self.show_alert()
