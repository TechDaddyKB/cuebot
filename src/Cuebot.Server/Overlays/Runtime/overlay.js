// Cuebot overlay runtime: sizes the stage to the overlay's canvas, runs each layer's widget in a frame served from
// the widget port (another origin: no access to this page, its token or Cuebot's API) and keeps a WebSocket to Cuebot open,
// reconnecting with backoff. Cuebot's messages go to the widgets (StreamElements' events and session data); the
// widgets' SE_API requests go to Cuebot and the answers back to the widget that asked. Every message is also
// dispatched as a "cuebot:message" window event.
(function () {
  "use strict";

  var config = window.CUEBOT_OVERLAY || {};
  var listeners = {};
  var socket = null;
  var retryMs = 1000;
  var pingTimer = null;
  var layers = {};
  var hello = null;
  var muted = false;

  var CURRENCY = { code: "USD", name: "U.S. Dollar", symbol: "$" };

  function emit(type, detail) {
    (listeners[type] || []).slice().forEach(function (fn) {
      try { fn(detail); } catch (e) { console.error("[Cuebot] listener failed", e); }
    });
    window.dispatchEvent(new CustomEvent("cuebot:message", { detail: detail }));
  }

  function base() {
    return "/o/" + encodeURIComponent(config.id);
  }

  function token() {
    return "token=" + encodeURIComponent(config.token);
  }

  function socketUrl() {
    var scheme = location.protocol === "https:" ? "wss:" : "ws:";
    return scheme + "//" + location.host + base() + "/ws?" + token();
  }

  function send(message) {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(typeof message === "string" ? message : JSON.stringify(message));
  }

  // ---- Widgets ---------------------------------------------------------------------------------------------------

  function postToLayer(entry, message) {
    if (entry.loaded && entry.frame.contentWindow) entry.frame.contentWindow.postMessage(message, "*");
  }

  function broadcast(message) {
    Object.keys(layers).forEach(function (id) { postToLayer(layers[id], message); });
  }

  function loadDetail(entry) {
    return {
      channel: { username: "", apiToken: "", id: "", providerId: "", avatar: "" },
      currency: CURRENCY,
      fieldData: entry.doc.fieldData || {},
      recents: (hello && hello.recents) || [],
      session: { data: (hello && hello.session) || {}, settings: { autoReset: true, calendar: true, resetOnStart: false } },
      overlay: { isEditorMode: false, muted: muted },
      emulated: false
    };
  }

  function startLayer(entry) {
    if (entry.loaded || !entry.frameReady || !hello) return;
    entry.loaded = true;
    entry.frame.contentWindow.postMessage({ type: "load", widgetDuration: entry.doc.widgetDuration || 0, detail: loadDetail(entry) }, "*");
  }

  function buildLayers(stage) {
    (config.layers || []).forEach(function (layer) {
      if (!layer.visible) return;
      var frame = document.createElement("iframe");
      // Scripts on the widget's own origin; no popups, forms or navigating the overlay page.
      frame.setAttribute("sandbox", "allow-scripts allow-same-origin");
      frame.setAttribute("allow", "autoplay");
      frame.setAttribute("title", layer.name || "Widget");
      frame.className = "cuebot-layer";
      frame.style.left = layer.x + "px";
      frame.style.top = layer.y + "px";
      frame.style.width = layer.width + "px";
      frame.style.height = layer.height + "px";
      frame.style.opacity = layer.opacity;
      if (layer.rotation) frame.style.transform = "rotate(" + layer.rotation + "deg)";
      stage.appendChild(frame);

      var entry = { layer: layer, frame: frame, doc: null, frameReady: false, loaded: false };
      layers[layer.id] = entry;
      fetch(base() + "/l/" + encodeURIComponent(layer.id) + "?" + token(), { cache: "no-store" })
        .then(function (r) { return r.json(); })
        .then(function (doc) {
          entry.doc = doc;
          frame.addEventListener("load", function () {
            entry.frameReady = true;
            startLayer(entry);
          }, { once: true });
          frame.src = location.protocol + "//" + location.hostname + ":" + config.widgetPort + doc.src;
        })
        .catch(function (e) { console.error("[Cuebot] could not load layer " + layer.name, e); });
    });
  }

  // Requests from widgets (SE_API) go to Cuebot, tagged with the layer that asked.
  window.addEventListener("message", function (e) {
    var data = e.data;
    if (!data || data.cuebot !== "request") return;
    var id = Object.keys(layers).find(function (key) { return layers[key].frame.contentWindow === e.source; });
    if (!id) return;
    send({ type: "request", id: data.id, layer: id, request: data.request, data: data.data });
  });

  // ---- Messages from Cuebot --------------------------------------------------------------------------------------

  function onMessage(message) {
    switch (message.type) {
      case "hello":
        hello = message;
        Object.keys(layers).forEach(function (id) { startLayer(layers[id]); });
        break;
      case "event":
        broadcast({ type: "event", listener: message.listener, event: message.event });
        break;
      case "session":
        if (hello) hello.session = message.session;
        broadcast({ type: "session", session: message.session });
        break;
      case "response":
        if (layers[message.layer]) postToLayer(layers[message.layer], message);
        break;
      case "command":
        if (message.command === "reload") {
          location.reload();
          return;
        }
        if (message.command === "mute" || message.command === "unmute") {
          muted = message.command === "mute";
          broadcast({ type: "event", listener: "alertService:toggleSound", event: { muted: muted } });
        }
        if (message.command === "skip") broadcast({ type: "event", listener: "event:skip", event: {} });
        break;
    }
    emit(message.type, message);
  }

  function connect() {
    socket = new WebSocket(socketUrl());
    socket.onopen = function () {
      retryMs = 1000;
      clearInterval(pingTimer);
      pingTimer = setInterval(function () { send("ping"); }, 25000);
    };
    socket.onmessage = function (e) {
      var message;
      try { message = JSON.parse(e.data); } catch (err) { return; }
      if (message && typeof message === "object") onMessage(message);
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

    /** Sends a text message to Cuebot. */
    send: function (text) {
      send(text);
    }
  };

  document.addEventListener("DOMContentLoaded", function () {
    var stage = document.getElementById("cuebot-stage");
    if (!stage) return;
    stage.style.width = config.width + "px";
    stage.style.height = config.height + "px";
    buildLayers(stage);
  });

  connect();
})();
