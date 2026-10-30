// Cuebot overlay runtime: sizes the stage to the overlay's canvas and keeps a WebSocket to Cuebot open, reconnecting
// with backoff. Every message is dispatched as a "cuebot:message" window event; widgets build on window.Cuebot.
(function () {
  "use strict";

  var config = window.CUEBOT_OVERLAY || {};
  var listeners = {};
  var socket = null;
  var retryMs = 1000;
  var pingTimer = null;

  function emit(type, detail) {
    (listeners[type] || []).slice().forEach(function (fn) {
      try { fn(detail); } catch (e) { console.error("[Cuebot] listener failed", e); }
    });
    window.dispatchEvent(new CustomEvent("cuebot:message", { detail: detail }));
  }

  function socketUrl() {
    var scheme = location.protocol === "https:" ? "wss:" : "ws:";
    return scheme + "//" + location.host + "/o/" + encodeURIComponent(config.id) + "/ws?token=" + encodeURIComponent(config.token);
  }

  function connect() {
    socket = new WebSocket(socketUrl());
    socket.onopen = function () {
      retryMs = 1000;
      clearInterval(pingTimer);
      pingTimer = setInterval(function () {
        if (socket && socket.readyState === WebSocket.OPEN) socket.send("ping");
      }, 25000);
    };
    socket.onmessage = function (e) {
      var message;
      try { message = JSON.parse(e.data); } catch (err) { return; }
      if (!message || typeof message !== "object") return;
      if (message.type === "command" && message.command === "reload") {
        location.reload();
        return;
      }
      emit(message.type, message);
    };
    socket.onclose = function () {
      clearInterval(pingTimer);
      socket = null;
      setTimeout(connect, retryMs);
      retryMs = Math.min(retryMs * 2, 30000);
    };
  }

  window.Cuebot = {
    overlay: { id: config.id, name: config.name, width: config.width, height: config.height },

    /** Calls fn(message) for every message of a type ("hello", "event", "session", "command"). */
    on: function (type, fn) {
      (listeners[type] = listeners[type] || []).push(fn);
    },

    /** Sends a text message to Cuebot (ignored until the widget runtime handles requests). */
    send: function (text) {
      if (socket && socket.readyState === WebSocket.OPEN) socket.send(text);
    }
  };

  document.addEventListener("DOMContentLoaded", function () {
    var stage = document.getElementById("cuebot-stage");
    if (stage) {
      stage.style.width = config.width + "px";
      stage.style.height = config.height + "px";
    }
  });

  connect();
})();
