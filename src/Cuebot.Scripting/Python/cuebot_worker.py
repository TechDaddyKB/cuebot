"""Cuebot Python worker.

Runs user scripts for the Cuebot host. The host talks to this process with one JSON object per line:

    host -> worker  {"type": "run", "id", "code", "args"}
                    {"type": "callResult", "callId", "result"} | {"type": "callError", "callId", "error"}
    worker -> host  {"type": "ready", "version"}
                    {"type": "call", "id", "callId", "name", "params"}
                    {"type": "print", "id", "text"}
                    {"type": "done", "id", "result"} | {"type": "error", "id", "error", "traceback"}

While a script waits for a call result the worker keeps serving, so a host call that runs another action
containing a Python script (a nested "run") works. The protocol uses a private copy of the original stdout;
file descriptor 1 is pointed at stderr and sys.stdout sends "print" messages, so user output never corrupts it.
"""

import ast
import io
import json
import linecache
import os
import platform
import sys
import traceback

SCRIPT_FILE = "<script>"
_WRAPPER = "def __cuebot_main__():\n    pass\n"


class CuebotError(Exception):
    """Raised in scripts when a Cuebot host function fails."""


class _HostClosed(BaseException):
    """The host closed the connection; the worker exits."""


class _Channel:
    """Line-delimited JSON over private copies of the process's stdin and stdout."""

    def __init__(self):
        self._input = io.TextIOWrapper(os.fdopen(os.dup(0), "rb"), encoding="utf-8")
        self._output = io.TextIOWrapper(os.fdopen(os.dup(1), "wb"), encoding="utf-8", newline="\n")
        os.dup2(2, 1)

    def send(self, message):
        self._output.write(json.dumps(message, default=str) + "\n")
        self._output.flush()

    def receive(self):
        line = self._input.readline()
        if not line:
            raise _HostClosed()
        return json.loads(line)


class _PrintStream(io.TextIOBase):
    """sys.stdout replacement: forwards complete lines to the host log."""

    def __init__(self, worker):
        self._worker = worker
        self._pending = ""

    def writable(self):
        return True

    def write(self, text):
        self._pending += text
        *lines, self._pending = self._pending.split("\n")
        for line in lines:
            self._worker.print(line)
        return len(text)

    def flush(self):
        if self._pending:
            self._worker.print(self._pending)
            self._pending = ""


class Cuebot:
    """Host helpers available to scripts as `cb`."""

    def __init__(self, worker, args):
        self._worker = worker
        self._args = dict(args)

    @property
    def args(self):
        """A snapshot of the action's arguments when the script started (updated by set_arg)."""
        return dict(self._args)

    def call(self, function, /, **params):
        """Calls any Cuebot script function by name, e.g. cb.call("obs.request", requestType="GetVersion")."""
        return self._worker.call(function, params)

    def get_arg(self, name, default=None):
        value = self.call("args.get", name=name)
        return default if value is None else value

    def set_arg(self, name, value):
        self.call("args.set", name=name, value=value)
        self._args[name] = value

    def get_global(self, name, default=None):
        value = self.call("globals.get", name=name)
        return default if value is None else value

    def set_global(self, name, value, persisted=True):
        self.call("globals.set", name=name, value=str(value), persisted=persisted)

    def log(self, message, level="Info"):
        self.call("log", message=str(message), level=level)

    def run_action(self, name):
        """Runs another action and waits for it; returns whether it succeeded."""
        return bool(self.call("actions.run", name=name))

    def send_chat(self, message):
        self.call("twitch.sendMessage", message=str(message))

    def speak(self, text, alias=None):
        self.call("speech.speak", text=str(text), alias=alias)

    def obs(self, request_type, data=None):
        """Sends an obs-websocket request and returns its response data."""
        return self.call("obs.request", requestType=request_type, requestData=data)


class Worker:
    def __init__(self, channel):
        self._channel = channel
        self._runs = []
        self._next_call_id = 0

    def serve(self, until_call_id=None):
        """Handles host messages until the given call is answered (or forever when None)."""
        while True:
            message = self._channel.receive()
            kind = message.get("type")
            if kind == "run":
                self._run(message)
            elif kind in ("callResult", "callError") and message.get("callId") == until_call_id:
                if kind == "callError":
                    raise CuebotError(message.get("error") or "Host call failed")
                return message.get("result")

    def call(self, name, params):
        self._next_call_id += 1
        call_id = str(self._next_call_id)
        self._channel.send({"type": "call", "id": self._current_run(), "callId": call_id, "name": name, "params": params})
        return self.serve(until_call_id=call_id)

    def print(self, text):
        self._channel.send({"type": "print", "id": self._current_run(), "text": text})

    def _current_run(self):
        return self._runs[-1] if self._runs else None

    def _run(self, message):
        run_id = message.get("id")
        self._runs.append(run_id)
        try:
            namespace = {"__name__": "__cuebot_script__", "cb": Cuebot(self, message.get("args") or {}), "CuebotError": CuebotError}
            result = _compile(message.get("code") or "", namespace)()
            sys.stdout.flush()
            self._channel.send({"type": "done", "id": run_id, "result": _to_json(result)})
        except _HostClosed:
            raise
        except BaseException as error:  # scripts may raise anything, including SystemExit
            sys.stdout.flush()
            self._channel.send({"type": "error", "id": run_id, "error": _describe(error), "traceback": _script_traceback(error)})
        finally:
            self._runs.pop()


def _compile(code, namespace):
    """Compiles the script as the body of a function, so `return False` stops the action."""
    linecache.cache[SCRIPT_FILE] = (len(code), None, code.splitlines(True), SCRIPT_FILE)
    script = ast.parse(code, SCRIPT_FILE)
    wrapper = ast.parse(_WRAPPER)
    if script.body:
        wrapper.body[0].body = script.body
    exec(compile(wrapper, SCRIPT_FILE, "exec"), namespace)
    return namespace.pop("__cuebot_main__")


def _to_json(value):
    try:
        json.dumps(value)
        return value
    except (TypeError, ValueError):
        return repr(value)


def _describe(error):
    if isinstance(error, SyntaxError):
        return f"SyntaxError at line {error.lineno}: {error.msg}"
    lines = [frame.lineno for frame in traceback.extract_tb(error.__traceback__) if frame.filename == SCRIPT_FILE]
    where = f" at line {lines[-1]}" if lines else ""
    return f"{type(error).__name__}{where}: {error}"


def _script_traceback(error):
    """The traceback starting at the first script frame (worker frames are hidden)."""
    tb = error.__traceback__
    while tb is not None and tb.tb_frame.f_code.co_filename != SCRIPT_FILE:
        tb = tb.tb_next
    return "".join(traceback.format_exception(type(error), error, tb))


def main():
    channel = _Channel()
    worker = Worker(channel)
    sys.stdout = _PrintStream(worker)
    sys.stdin = io.StringIO()
    channel.send({"type": "ready", "version": platform.python_version()})
    try:
        worker.serve()
    except _HostClosed:
        pass


if __name__ == "__main__":
    main()
