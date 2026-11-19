"""Tests for the StreamController plugin's connection and key logic (no StreamController or GTK needed)."""

import json
import os
import queue
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "io_github_cuebot_Cuebot"))

import pytest  # noqa: E402

from cuebot_core import (  # noqa: E402
    ButtonInfo,
    Connection,
    CuebotClient,
    KeyRegistry,
    RequestError,
    action_choices,
    connection_from,
    decode_data_uri,
    device_type_number,
    find_action,
    friendly_error,
    new_button_id,
    normalize_id,
    parse_args,
    parse_server_message,
)


# ---- helpers ---------------------------------------------------------------------------------------------------------


def test_connection_defaults_and_errors():
    c = connection_from({"token": " abc "})
    assert isinstance(c, Connection)
    assert c.url == "ws://127.0.0.1:7475/api/v1/streamdeck"
    assert c.token == "abc"
    assert connection_from({"host": "::1", "port": "8000", "token": "t"}).url == "ws://[::1]:8000/api/v1/streamdeck"
    assert "token" in connection_from({})
    assert "not a valid port" in connection_from({"port": "x", "token": "t"})
    assert "not a valid port" in connection_from({"port": "70000", "token": "t"})
    assert "not a host" in connection_from({"host": "a b", "token": "t"})


def test_parse_args_lines_and_json():
    assert parse_args("a=1\n# skip\n\nb = two words\nnoequals") == {"a": "1", "b": "two words"}
    assert parse_args('{"a": 1, "b": "x"}') == {"a": "1", "b": "x"}
    assert parse_args("") == {}
    assert parse_args(None) == {}


def test_button_ids():
    assert new_button_id("trigger", lambda: 0.5) == "key-888888"
    assert new_button_id("run", lambda: 0.0) == "run-000000"
    assert normalize_id("  Go-Live ") == "go-live"


def test_action_choices_and_find():
    actions = [{"id": "2", "name": "b", "group": "", "enabled": True},
               {"id": "1", "name": "A", "group": "Alerts", "enabled": False}]
    assert action_choices(actions) == [("2", "b", True), ("1", "Alerts / A", False)]
    assert find_action(actions, "1", None)["name"] == "A"
    assert find_action(actions, "missing", "B")["id"] == "2"
    assert find_action(actions, None, None) is None


def test_misc():
    assert device_type_number("Stream Deck XL") == 2
    assert device_type_number("Stream Deck +") == 7
    assert device_type_number("Stream Deck Original") == 0
    assert decode_data_uri("data:image/png;base64,aGk=") == b"hi"
    assert decode_data_uri("data:text/plain,a%20b") == b"a b"
    assert "token" in friendly_error("Handshake status 401 Unauthorized")
    assert "not running" in friendly_error("[Errno 111] Connection refused")
    assert "quit" in friendly_error("[Errno 104] Connection reset by peer")
    assert parse_server_message("[]") is None
    assert parse_server_message('{"type": "pong"}') == {"type": "pong"}


# ---- client ----------------------------------------------------------------------------------------------------------


class FakeSocket:
    """A fake Cuebot: records what the plugin sends and answers like the server."""

    def __init__(self, auto_welcome=True):
        self.sent = []
        self.incoming = queue.Queue()
        self.closed = False
        if auto_welcome:
            self.incoming.put(json.dumps({"type": "welcome", "protocol": 1, "session": "s", "app": {"name": "Cuebot", "version": "9.9"}}))

    def send(self, text):
        message = json.loads(text)
        self.sent.append(message)
        if message.get("type") == "request":
            if message["method"] == "getActions":
                self.push({"type": "response", "id": message["id"], "ok": True, "result": {"actions": [{"id": "a1", "name": "Hello"}]}})
            elif message["method"] == "fail":
                self.push({"type": "response", "id": message["id"], "ok": False, "error": "nope"})

    def push(self, message):
        self.incoming.put(json.dumps(message))

    def recv(self):
        item = self.incoming.get()
        if item is None:
            raise ConnectionError("closed")
        return item

    def close(self):
        self.closed = True
        self.incoming.put(None)


def wait_for(predicate, timeout=3.0):
    end = time.time() + timeout
    while time.time() < end:
        if predicate():
            return True
        time.sleep(0.01)
    return False


def make_client(sockets):
    made = []

    def factory(url, token):
        sock = sockets.pop(0) if sockets else FakeSocket()
        sock.url, sock.token = url, token
        made.append(sock)
        return sock

    client = CuebotClient(hello=lambda: {"plugin": {"uuid": "x", "version": "1"}, "app": {"name": "StreamController"},
                                         "devices": [], "buttons": []},
                          socket_factory=factory, request_timeout=2, max_backoff=0.05)
    return client, made


def test_client_says_hello_and_connects():
    client, made = make_client([])
    statuses = []
    client.status_listeners.append(lambda s: statuses.append(s.state))
    client.configure(connection_from({"token": "tok"}))
    assert wait_for(lambda: client.is_connected)
    assert made[0].token == "tok"
    assert made[0].sent[0]["type"] == "hello"
    assert made[0].sent[0]["protocol"] == 1
    assert client.status.message == "Connected to Cuebot 9.9"
    assert "connecting" in statuses
    client.stop()


def test_requests_answer_and_fail():
    client, made = make_client([])
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: client.is_connected)
    assert client.request("getActions") == {"actions": [{"id": "a1", "name": "Hello"}]}
    with pytest.raises(RequestError, match="nope"):
        client.request("fail")
    with pytest.raises(RequestError, match="in time"):
        client.request("silent")
    client.stop()


def test_request_before_connect_fails_fast():
    client, _ = make_client([])
    with pytest.raises(RequestError):
        client.request("getActions")
    assert client.send({"type": "ping"}) is False


def test_pushes_reach_listeners():
    client, made = make_client([])
    got = {"command": [], "actions": [], "global": []}
    client.command_listeners.append(lambda c: got["command"].append(c))
    client.actions_listeners.append(lambda a: got["actions"].append(a))
    client.global_listeners.append(lambda n, v: got["global"].append((n, v)))
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: client.is_connected)
    made[0].push({"type": "command", "command": "showOk", "buttonId": "b"})
    made[0].push({"type": "actionsChanged", "actions": [{"id": "1"}]})
    made[0].push({"type": "globalChanged", "name": "deaths", "value": 3})
    assert wait_for(lambda: got["command"] and got["actions"] and got["global"])
    assert got["global"] == [("deaths", 3)]
    client.stop()


def test_reconnects_after_close_and_on_new_settings():
    client, made = make_client([])
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: client.is_connected)
    made[0].close()
    assert wait_for(lambda: len(made) >= 2 and client.is_connected)
    client.configure(connection_from({"token": "other"}))
    assert wait_for(lambda: made[-1].token == "other" and client.is_connected)
    client.configure(connection_from({}))
    assert wait_for(lambda: client.status.state == "error")
    client.stop()


def test_connect_errors_retry_with_a_reason():
    attempts = []

    def factory(url, token):
        attempts.append(1)
        raise OSError("[Errno 111] Connection refused")

    client = CuebotClient(hello=lambda: {}, socket_factory=factory, max_backoff=0.05)
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: len(attempts) >= 3)
    assert "not running" in client.status.message
    client.stop()


def test_newer_protocol_asks_for_an_update():
    sock = FakeSocket(auto_welcome=False)
    sock.push({"type": "welcome", "protocol": 2, "app": {"version": "x"}})
    client, _ = make_client([sock])
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: client.is_connected)
    assert "update the plugin" in client.status.message
    client.stop()


# ---- registry --------------------------------------------------------------------------------------------------------


class FakeKey:
    def __init__(self, is_dial=False):
        self.is_dial = is_dial
        self.calls = []

    def set_title(self, title, state=None):
        self.calls.append(("title", title, state))

    def set_state(self, state):
        self.calls.append(("state", state))

    def set_image(self, image, state=None):
        self.calls.append(("image", image, state))

    def set_value(self, value, indicator):
        self.calls.append(("value", value, indicator))

    def show_alert(self):
        self.calls.append(("alert",))

    def show_ok(self):
        self.calls.append(("ok",))


def info(context, button_id, kind="trigger", controller="Keypad"):
    return ButtonInfo(context, button_id, kind, controller, "deck")


def test_commands_reach_every_key_with_the_id():
    reg = KeyRegistry()
    a, b, c = FakeKey(), FakeKey(), FakeKey()
    reg.add(info("1", "Go-Live"), a)
    reg.add(info("2", "go-live"), b)
    reg.add(info("3", "other"), c)
    assert reg.apply({"command": "setTitle", "buttonId": " GO-LIVE ", "title": "LIVE"}) == 2
    assert a.calls == [("title", "LIVE", None)] and b.calls == a.calls and c.calls == []
    assert reg.apply({"command": "setTitle", "buttonId": ""}) == 0


def test_appearance_is_replayed_for_keys_that_appear_later():
    reg = KeyRegistry()
    reg.apply({"command": "setTitle", "buttonId": "x", "title": "A", "state": 1})
    reg.apply({"command": "setState", "buttonId": "x", "state": 1})
    reg.apply({"command": "showOk", "buttonId": "x"})
    key = FakeKey()
    reg.add(info("1", "x"), key)
    assert ("title", "A", 1) in key.calls and ("state", 1) in key.calls and ("ok",) not in key.calls
    reg.apply({"command": "setTitle", "buttonId": "x", "title": "B"})   # all states replaces the per-state one
    later = FakeKey()
    reg.add(info("2", "x"), later)
    assert ("title", "A", 1) not in later.calls and ("title", "B", None) in later.calls


def test_toggle_state_flips_and_dials_get_values():
    reg = KeyRegistry()
    key, dial = FakeKey(), FakeKey(is_dial=True)
    reg.add(info("k", "x"), key)
    reg.add(info("d", "x", controller="Encoder"), dial)
    reg.apply({"command": "toggleState", "buttonId": "x"})
    assert ("state", 1) in key.calls and reg.get("k").state == 1
    reg.apply({"command": "toggleState", "buttonId": "x"})
    assert key.calls[-1] == ("state", 0)
    reg.apply({"command": "setValue", "buttonId": "x", "value": "42", "indicator": 150})
    assert dial.calls[-1] == ("value", "42", 100)
    reg.apply({"command": "showOk", "buttonId": "x"})
    assert ("ok",) not in dial.calls and key.calls[-1] == ("ok",)


def test_image_urls_are_loaded_or_alert():
    reg = KeyRegistry(load_image=lambda url: "data:image/png;base64,AA==" if "good" in url else (_ for _ in ()).throw(OSError("x")))
    key = FakeKey()
    reg.add(info("k", "x"), key)
    reg.apply({"command": "setImage", "buttonId": "x", "image": "https://good/pic.png"})
    assert key.calls[-1] == ("image", "data:image/png;base64,AA==", None)
    reg.apply({"command": "setImage", "buttonId": "x", "image": "https://bad/pic.png"})
    assert key.calls[-1] == ("alert",)


def test_list_update_and_remove():
    reg = KeyRegistry()
    reg.add(ButtonInfo("1", "a", "run", "Keypad", "deck", row=1, column=2), FakeKey())
    assert reg.list() == [{"context": "1", "buttonId": "a", "kind": "run", "controller": "Keypad", "device": "deck",
                           "state": 0, "isInMultiAction": False, "row": 1, "column": 2}]
    reg.update_button_id("1", "b")
    assert reg.get("1").button_id == "b"
    assert reg.remove("1") and not reg.remove("1") and len(reg) == 0


def test_key_errors_do_not_stop_other_keys():
    reg = KeyRegistry()
    broken = FakeKey()
    broken.set_title = lambda *a: (_ for _ in ()).throw(RuntimeError("gone"))
    ok = FakeKey()
    reg.add(info("1", "x"), broken)
    reg.add(info("2", "x"), ok)
    assert reg.apply({"command": "setTitle", "buttonId": "x", "title": "T"}) == 2
    assert ok.calls == [("title", "T", None)]


def test_threads_are_daemons():
    client, _ = make_client([])
    client.configure(connection_from({"token": "t"}))
    assert wait_for(lambda: client.is_connected)
    assert all(t.daemon for t in threading.enumerate() if t.name == "cuebot-connection")
    client.stop()
