"""
Cuebot's Stream Deck protocol (version 1) for StreamController: the connection to Cuebot and the visible keys.

The plugin connects to ws://host:port/api/v1/streamdeck on Cuebot's REST API listener with
"Authorization: Bearer <token>", like Cuebot's Elgato plugin (streamdeck/ in the Cuebot repo). The message format is
in src/Cuebot.StreamDeck/PROTOCOL.md. Nothing here imports StreamController or GTK, so it is unit-tested on its own
(streamcontroller/tests).
"""

from __future__ import annotations

import base64
import json
import random
import threading
from dataclasses import dataclass, replace
from typing import Any, Callable, Optional, Protocol

PROTOCOL_VERSION = 1
PLUGIN_UUID = "io.github.cuebot.streamcontroller"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 7475
KINDS = ("run", "toggle", "global", "trigger")

# Elgato's DeviceType numbers (Cuebot shows them as names), by the deck type names StreamController reports.
DEVICE_TYPES = {
    "stream deck mini": 1,
    "stream deck xl": 2,
    "stream deck pedal": 5,
    "stream deck +": 7,
    "stream deck plus": 7,
    "stream deck neo": 9,
    "stream deck studio": 10,
}


def device_type_number(name: str) -> int:
    """Elgato's device type for a deck type name; 0 (Stream Deck) when unknown."""
    text = (name or "").strip().lower()
    if text in DEVICE_TYPES:
        return DEVICE_TYPES[text]
    for key, number in DEVICE_TYPES.items():
        if text.startswith(key):
            return number
    return 0


# ---- Settings helpers ------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Connection:
    host: str
    port: int
    token: str
    url: str


def connection_from(settings: dict) -> Connection | str:
    """Normalizes the plugin settings into a connection, or returns why it cannot connect yet."""
    host = str(settings.get("host") or "").strip() or DEFAULT_HOST
    port_text = str(settings.get("port") or "").strip()
    token = str(settings.get("token") or "").strip()
    try:
        port = DEFAULT_PORT if port_text == "" else int(port_text)
    except ValueError:
        return f'Port "{port_text}" is not a valid port'
    if port < 1 or port > 65535:
        return f'Port "{port_text}" is not a valid port'
    if token == "":
        return "Paste the API token from Cuebot (Input > Stream Deck) into the Cuebot plugin settings"
    if any(c.isspace() or c == "/" for c in host):
        return f'Host "{host}" is not a host name or IP address'
    host_part = f"[{host}]" if ":" in host and not host.startswith("[") else host
    return Connection(host, port, token, f"ws://{host_part}:{port}/api/v1/streamdeck")


def parse_args(text: str | None) -> dict[str, str]:
    """Key arguments: one name=value per line (blank lines and # comments skipped), or a JSON object."""
    result: dict[str, str] = {}
    trimmed = (text or "").strip()
    if trimmed == "":
        return result
    if trimmed.startswith("{"):
        try:
            parsed = json.loads(trimmed)
            if isinstance(parsed, dict):
                for k, v in parsed.items():
                    result[str(k)] = v if isinstance(v, str) else json.dumps(v)
                return result
        except ValueError:
            pass
    for raw in trimmed.splitlines():
        line = raw.strip()
        if line == "" or line.startswith("#"):
            continue
        eq = line.find("=")
        if eq <= 0:
            continue
        result[line[:eq].strip()] = line[eq + 1:].strip()
    return result


def new_button_id(kind: str, rng: Callable[[], float] = random.random) -> str:
    """A short, readable button id, e.g. "key-7f3a9c"."""
    prefix = "key" if kind == "trigger" else kind
    return prefix + "-" + "".join(format(int(rng() * 16) % 16, "x") for _ in range(6))


def normalize_id(button_id: str | None) -> str:
    """Button ids are matched case-insensitively and without surrounding spaces."""
    return (button_id or "").strip().lower()


def action_choices(actions: list[dict]) -> list[tuple[str, str, bool]]:
    """(id, "Group / Name", enabled) for an action picker, sorted by group then name."""
    ordered = sorted(actions, key=lambda a: ((a.get("group") or "").lower(), (a.get("name") or "").lower()))
    return [
        (str(a.get("id", "")), f"{a['group']} / {a.get('name', '')}" if a.get("group") else str(a.get("name", "")),
         bool(a.get("enabled", True)))
        for a in ordered
    ]


def find_action(actions: list[dict], action_id: str | None, action_name: str | None) -> Optional[dict]:
    """The action by id, else by name (case-insensitive)."""
    for a in actions:
        if action_id and a.get("id") == action_id:
            return a
    name = (action_name or "").strip().lower()
    if name:
        for a in actions:
            if str(a.get("name", "")).lower() == name:
                return a
    return None


def value_text(value: Any) -> str:
    return value if isinstance(value, str) else json.dumps(value)


def decode_data_uri(uri: str) -> bytes:
    """The bytes of a data: URI (base64 or plain)."""
    if not uri.startswith("data:") or "," not in uri:
        raise ValueError("not a data URI")
    header, payload = uri[5:].split(",", 1)
    if header.endswith(";base64"):
        return base64.b64decode(payload)
    from urllib.parse import unquote_to_bytes
    return unquote_to_bytes(payload)


def friendly_error(text: str) -> str:
    """Turns socket errors into something a streamer can act on."""
    if "401" in text:
        return "Cuebot refused the token (copy it again from Cuebot's Stream Deck page)"
    if "403" in text:
        return "Cuebot refused the connection (origin not allowed)"
    if "404" in text:
        return "Cuebot's REST API is off or too old (turn on the REST API on Cuebot's Stream Deck page)"
    if "Connection reset" in text or "Errno 104" in text or "Connection to remote host was lost" in text:
        return "Cuebot closed the connection (did it quit?)"
    if "Connection refused" in text or "Errno 111" in text:
        return "Cuebot is not running or its REST API is off"
    if "Name or service not known" in text or "Temporary failure in name resolution" in text:
        return "Host not found"
    return text


# ---- Client ----------------------------------------------------------------------------------------------------------


class Socket(Protocol):
    def send(self, text: str) -> None: ...
    def recv(self) -> str: ...
    def close(self) -> None: ...


SocketFactory = Callable[[str, str], Socket]


def default_socket(url: str, token: str) -> Socket:
    """A websocket-client connection (StreamController ships websocket-client)."""
    import websocket  # imported here so the tests can run without it

    ws = websocket.WebSocket()
    ws.connect(url, header=[f"Authorization: Bearer {token}"], timeout=5, redirect_limit=0)
    ws.settimeout(None)
    return ws


@dataclass
class Status:
    state: str  # disconnected, connecting, connected, error
    message: str
    cuebot_version: str = ""


class RequestError(Exception):
    pass


class CuebotClient:
    """
    One connection to Cuebot on a background thread, with automatic reconnects (1 s doubling to 30 s). request()
    blocks until Cuebot answers (call it off the UI thread). Commands and pushes go to the listeners, on the
    connection's thread.
    """

    def __init__(self, hello: Callable[[], dict], socket_factory: SocketFactory = default_socket,
                 request_timeout: float = 10.0, max_backoff: float = 30.0, sleep: Callable[[float], None] = None):
        self._hello = hello
        self._factory = socket_factory
        self._request_timeout = request_timeout
        self._max_backoff = max_backoff
        self._lock = threading.RLock()
        self._send_lock = threading.Lock()
        self._wake = threading.Event()
        self._connection: Connection | None = None
        self._generation = 0
        self._socket: Socket | None = None
        self._welcomed = False
        self._thread: threading.Thread | None = None
        self._next_id = 1
        self._pending: dict[int, dict] = {}
        self.status = Status("disconnected", "Not configured")
        self.status_listeners: list[Callable[[Status], None]] = []
        self.command_listeners: list[Callable[[dict], None]] = []
        self.actions_listeners: list[Callable[[list], None]] = []
        self.global_listeners: list[Callable[[str, Any], None]] = []
        self._stopped = False

    @property
    def is_connected(self) -> bool:
        return self.status.state == "connected"

    def configure(self, connection: Connection | str) -> None:
        """Connects with new settings (or reconnects when they changed); a string stops and shows it."""
        with self._lock:
            if isinstance(connection, str):
                self._connection = None
                self._generation += 1
                self._close_socket()
                self._set_status(Status("error", connection))
                self._wake.set()
                return
            same = self._connection == connection
            self._connection = connection
            if same and self._socket is not None:
                return
            self._generation += 1
            self._close_socket()
            self._wake.set()
            if self._thread is None or not self._thread.is_alive():
                self._thread = threading.Thread(target=self._run, name="cuebot-connection", daemon=True)
                self._thread.start()

    def reconnect(self) -> None:
        with self._lock:
            if self._connection is None:
                return
            self._generation += 1
            self._close_socket()
            self._wake.set()

    def stop(self) -> None:
        with self._lock:
            self._stopped = True
            self._connection = None
            self._generation += 1
            self._close_socket()
            self._wake.set()
        self._set_status(Status("disconnected", "Stopped"))

    def send(self, message: dict) -> bool:
        """Sends a message when connected; False otherwise."""
        sock = self._socket
        if sock is None or not self._welcomed:
            return False
        try:
            with self._send_lock:
                sock.send(json.dumps(message))
            return True
        except Exception:
            return False

    def send_input(self, **fields) -> bool:
        return self.send({"type": "input", **{k: v for k, v in fields.items() if v is not None}})

    def request(self, method: str, params: dict | None = None) -> Any:
        """Sends a request and waits for the answer; raises RequestError with Cuebot's error text."""
        if self._socket is None or not self._welcomed:
            raise RequestError(self.status.message or "Not connected to Cuebot")
        with self._lock:
            request_id = self._next_id
            self._next_id += 1
            entry = {"event": threading.Event(), "ok": False, "result": None, "error": None}
            self._pending[request_id] = entry
        if not self.send({"type": "request", "id": request_id, "method": method, "params": params or {}}):
            self._pending.pop(request_id, None)
            raise RequestError("Not connected to Cuebot")
        if not entry["event"].wait(self._request_timeout):
            self._pending.pop(request_id, None)
            raise RequestError(f"Cuebot did not answer {method} in time")
        if entry["ok"]:
            return entry["result"]
        raise RequestError(entry["error"] or "Request failed")

    # -- connection thread --

    def _run(self) -> None:
        attempts = 0
        while True:
            with self._lock:
                if self._stopped:
                    return
                connection = self._connection
                generation = self._generation
                self._wake.clear()
            if connection is None:
                self._wake.wait()
                continue
            self._set_status(Status("connecting", f"Connecting to {connection.host}:{connection.port}..."))
            reason = ""
            try:
                sock = self._factory(connection.url, connection.token)
            except Exception as e:  # noqa: BLE001 - shown to the user
                reason = friendly_error(str(e))
                sock = None
            if sock is not None:
                with self._lock:
                    if generation != self._generation:
                        _close_quietly(sock)
                        continue
                    self._socket = sock
                    self._welcomed = False
                try:
                    with self._send_lock:
                        sock.send(json.dumps({"type": "hello", "protocol": PROTOCOL_VERSION, **self._hello()}))
                    while True:
                        text = sock.recv()
                        if text is None or text == "":
                            break
                        if generation != self._generation:
                            break
                        message = parse_server_message(text if isinstance(text, str) else text.decode("utf-8"))
                        if message is not None:
                            if message.get("type") == "welcome":
                                attempts = 0
                            self._handle(message)
                    reason = "Cuebot closed the connection"
                except Exception as e:  # noqa: BLE001
                    reason = friendly_error(str(e)) or "Connection to Cuebot closed"
                finally:
                    with self._lock:
                        if self._socket is sock:
                            self._socket = None
                            self._welcomed = False
                    _close_quietly(sock)
                    self._reject_pending("Connection to Cuebot closed")
            with self._lock:
                if self._stopped:
                    return
                if generation != self._generation:
                    attempts = 0
                    continue
            delay = min(self._max_backoff, 2 ** attempts)
            attempts += 1
            self._set_status(Status("error", f"{reason}; retrying in {int(round(delay))} s"))
            self._wake.wait(delay)

    def _handle(self, message: dict) -> None:
        kind = message.get("type")
        if kind == "welcome":
            self._welcomed = True
            version = str((message.get("app") or {}).get("version") or "")
            if message.get("protocol") == PROTOCOL_VERSION:
                text = f"Connected to Cuebot {version}".strip()
            else:
                text = (f"Connected, but Cuebot speaks protocol {message.get('protocol')} "
                        f"(plugin: {PROTOCOL_VERSION}); update the plugin")
            self._set_status(Status("connected", text, version))
        elif kind == "response":
            entry = self._pending.pop(message.get("id"), None)
            if entry is None:
                return
            entry["ok"] = bool(message.get("ok"))
            entry["result"] = message.get("result")
            entry["error"] = message.get("error")
            entry["event"].set()
        elif kind == "command":
            for listener in list(self.command_listeners):
                _call(listener, message)
        elif kind == "actionsChanged":
            for listener in list(self.actions_listeners):
                _call(listener, message.get("actions") or [])
        elif kind == "globalChanged":
            for listener in list(self.global_listeners):
                _call(listener, str(message.get("name") or ""), message.get("value"))

    def _reject_pending(self, reason: str) -> None:
        with self._lock:
            pending = list(self._pending.values())
            self._pending.clear()
        for entry in pending:
            entry["ok"] = False
            entry["error"] = reason
            entry["event"].set()

    def _close_socket(self) -> None:
        sock = self._socket
        self._socket = None
        self._welcomed = False
        if sock is not None:
            _close_quietly(sock)

    def _set_status(self, status: Status) -> None:
        self.status = status
        for listener in list(self.status_listeners):
            _call(listener, status)


def parse_server_message(text: str) -> Optional[dict]:
    """A server message, or None for anything that is not a JSON object with a string type."""
    try:
        value = json.loads(text)
    except ValueError:
        return None
    if isinstance(value, dict) and isinstance(value.get("type"), str):
        return value
    return None


def _close_quietly(sock: Socket) -> None:
    try:
        sock.close()
    except Exception:
        pass


def _call(listener: Callable, *args) -> None:
    try:
        listener(*args)
    except Exception:
        pass


# ---- Keys ------------------------------------------------------------------------------------------------------------


class KeyTarget(Protocol):
    """What a visible key or dial can show (the StreamController action in the plugin, a fake in tests)."""

    is_dial: bool

    def set_title(self, title: Optional[str], state: Optional[int] = None) -> None: ...
    def set_state(self, state: int) -> None: ...
    def set_image(self, image: Optional[str], state: Optional[int] = None) -> None: ...
    def set_value(self, value: str, indicator: Optional[int]) -> None: ...
    def show_alert(self) -> None: ...
    def show_ok(self) -> None: ...


@dataclass
class ButtonInfo:
    context: str
    button_id: str
    kind: str
    controller: str  # Keypad or Encoder
    device: str
    row: Optional[int] = None
    column: Optional[int] = None
    state: int = 0
    is_in_multi_action: bool = False

    def to_json(self) -> dict:
        d = {"context": self.context, "buttonId": self.button_id, "kind": self.kind, "controller": self.controller,
             "device": self.device, "state": self.state, "isInMultiAction": self.is_in_multi_action}
        if self.row is not None:
            d["row"] = self.row
        if self.column is not None:
            d["column"] = self.column
        return d


@dataclass
class _Entry:
    info: ButtonInfo
    target: KeyTarget


class KeyRegistry:
    """
    The visible Cuebot keys and dials, by context. Applies Cuebot's commands to every key with the button id and
    remembers the latest appearance per button id, so keys on another page show it when they appear.
    """

    def __init__(self, load_image: Callable[[str], str] | None = None, on_error: Callable[[str], None] | None = None):
        self._entries: dict[str, _Entry] = {}
        self._appearance: dict[str, dict[str, dict]] = {}
        self._load_image = load_image
        self._on_error = on_error
        self._lock = threading.RLock()

    def __len__(self) -> int:
        return len(self._entries)

    def list(self) -> list[dict]:
        with self._lock:
            return [e.info.to_json() for e in self._entries.values()]

    def get(self, context: str) -> Optional[ButtonInfo]:
        with self._lock:
            entry = self._entries.get(context)
            return replace(entry.info) if entry else None

    def add(self, info: ButtonInfo, target: KeyTarget) -> None:
        with self._lock:
            self._entries[info.context] = _Entry(replace(info), target)
        self._replay(info.context)

    def update_button_id(self, context: str, button_id: str) -> None:
        with self._lock:
            entry = self._entries.get(context)
            if entry is None:
                return
            changed = normalize_id(button_id) != normalize_id(entry.info.button_id)
            entry.info.button_id = button_id
        if changed:
            self._replay(context)

    def remove(self, context: str) -> bool:
        with self._lock:
            return self._entries.pop(context, None) is not None

    def set_local_state(self, context: str, state: int) -> None:
        with self._lock:
            entry = self._entries.get(context)
            if entry:
                entry.info.state = state

    def clear_appearance(self) -> None:
        with self._lock:
            self._appearance.clear()

    def apply(self, command: dict) -> int:
        """Applies a command to every key with its button id; returns how many keys it reached."""
        bid = normalize_id(command.get("buttonId"))
        if bid == "":
            return 0
        with self._lock:
            matches = [e for e in self._entries.values() if normalize_id(e.info.button_id) == bid]
            resolved = dict(command)
            if command.get("command") == "toggleState":
                remembered = self._appearance.get(bid, {}).get("setState:", {}).get("state")
                current = remembered if remembered is not None else (matches[0].info.state if matches else 0)
                resolved = {"type": "command", "command": "setState", "buttonId": command.get("buttonId"),
                            "state": 1 if current == 0 else 0}
        image = resolved.get("image")
        if resolved.get("command") == "setImage" and isinstance(image, str) and image.lower().startswith(("http://", "https://")):
            loaded = self._load(image)
            if loaded is None:
                for m in matches:
                    _call(m.target.show_alert)
                return len(matches)
            resolved["image"] = loaded
        with self._lock:
            self._remember(bid, resolved)
        for m in matches:
            self._apply_to(m, resolved)
        return len(matches)

    def _replay(self, context: str) -> None:
        with self._lock:
            entry = self._entries.get(context)
            if entry is None:
                return
            remembered = list(self._appearance.get(normalize_id(entry.info.button_id), {}).values())
        for command in remembered:
            self._apply_to(entry, command)

    def _remember(self, bid: str, command: dict) -> None:
        name = command.get("command")
        if name in ("showAlert", "showOk"):
            return
        memory = self._appearance.setdefault(bid, {})
        if name in ("setState", "setValue"):
            key = f"{name}:"
        else:
            state = command.get("state")
            key = f"{name}:{'' if state is None else state}"
        if name in ("setTitle", "setImage") and command.get("state") is None:
            for k in [k for k in memory if k.startswith(f"{name}:")]:
                del memory[k]
        memory[key] = command

    def _apply_to(self, entry: _Entry, c: dict) -> None:
        t = entry.target
        name = c.get("command")
        if name == "setTitle":
            _call(t.set_title, _empty_to_none(c.get("title")), None if t.is_dial else c.get("state"))
        elif name == "setImage":
            _call(t.set_image, _empty_to_none(c.get("image")), None if t.is_dial else c.get("state"))
        elif name == "setState":
            state = 1 if c.get("state") == 1 else 0
            entry.info.state = state
            if not t.is_dial:
                _call(t.set_state, state)
        elif name == "setValue":
            indicator = c.get("indicator")
            if isinstance(indicator, (int, float)):
                indicator = max(0, min(100, int(indicator)))
            else:
                indicator = None
            _call(t.set_value, str(c.get("value") or ""), indicator)
        elif name == "showAlert":
            _call(t.show_alert)
        elif name == "showOk":
            if not t.is_dial:
                _call(t.show_ok)

    def _load(self, url: str) -> Optional[str]:
        if self._load_image is None:
            return url
        try:
            return self._load_image(url)
        except Exception as e:  # noqa: BLE001
            if self._on_error:
                _call(self._on_error, f"Could not load image {url}: {e}")
            return None


def _empty_to_none(text: Optional[str]) -> Optional[str]:
    return None if text is None or text == "" else text


def fetch_image_as_data_uri(url: str, max_bytes: int = 2 * 1024 * 1024) -> str:
    """Downloads an image and returns it as a data URI (max 2 MB, image/* only)."""
    import requests

    response = requests.get(url, timeout=10, stream=True, allow_redirects=True)
    if response.status_code != 200:
        raise RuntimeError(f"HTTP {response.status_code}")
    kind = (response.headers.get("content-type") or "").split(";")[0].strip().lower()
    if not kind.startswith("image/"):
        raise RuntimeError(f"not an image ({kind or 'no content type'})")
    data = b""
    for chunk in response.iter_content(65536):
        data += chunk
        if len(data) > max_bytes:
            raise RuntimeError(f"image is larger than {max_bytes // 1024} KB")
    return f"data:{kind};base64,{base64.b64encode(data).decode('ascii')}"
