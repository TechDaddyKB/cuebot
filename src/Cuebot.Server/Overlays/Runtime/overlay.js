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

  /** Only http(s) addresses and Cuebot's own assets are used for media. */
  function safeUrl(url) {
    url = String(url || "").trim();
    return /^https?:\/\//i.test(url) || url.indexOf("/assets/") === 0 ? url : "";
  }

  /**
   * The positioned box every layer lives in: place, size, opacity and its enter animation; content goes in
   * box.inner, which carries the rotation (so the animation's transforms do not undo it).
   */
  function layerBox(layer) {
    var box = document.createElement("div");
    box.className = "cuebot-layer";
    box.dataset.layer = layer.id;
    box.style.left = layer.x + "px";
    box.style.top = layer.y + "px";
    box.style.width = layer.width + "px";
    box.style.height = layer.height + "px";
    box.style.opacity = layer.opacity;
    var enter = layer.enter || {};
    if (enter.name && enter.name !== "none") box.style.animation = "cuebot-" + enter.name + " " + (Number(enter.duration) || 1) + "s both";
    box.inner = document.createElement("div");
    box.inner.className = "cuebot-turn";
    if (layer.rotation) box.inner.style.transform = "rotate(" + layer.rotation + "deg)";
    box.appendChild(box.inner);
    return box;
  }

  function fontLink(family) {
    if (!family || document.querySelector('link[data-font="' + family + '"]')) return;
    var link = document.createElement("link");
    link.rel = "stylesheet";
    link.dataset.font = family;
    link.href = "https://fonts.googleapis.com/css2?family=" + encodeURIComponent(family).replace(/%20/g, "+") + ":wght@400;600;700;900&display=swap";
    document.head.appendChild(link);
  }

  function media(tag, url, s) {
    var el = document.createElement(tag);
    el.className = "cuebot-media";
    el.style.objectFit = ["contain", "cover", "fill"].indexOf(s.fit) !== -1 ? s.fit : "contain";
    if (url) el.src = url;
    return el;
  }

  /** Text, image, video and slideshow layers, drawn here from their settings. */
  function staticLayer(box, layer) {
    var s = layer.settings || {};
    switch (layer.type) {
      case "text":
        var text = document.createElement("div");
        text.className = "cuebot-text";
        text.textContent = s.text || "";
        fontLink(s.font);
        text.style.fontFamily = "'" + String(s.font || "Inter").replace(/'/g, "") + "', system-ui, sans-serif";
        text.style.fontSize = (Number(s.size) || 48) + "px";
        text.style.color = s.color || "#ffffff";
        text.style.fontWeight = s.weight || "700";
        text.style.textAlign = ["left", "center", "right"].indexOf(s.align) !== -1 ? s.align : "center";
        text.style.justifyContent = { left: "flex-start", right: "flex-end" }[s.align] || "center";
        text.style.padding = (Number(s.padding) || 0) + "px";
        if (s.background) text.style.background = s.background;
        if (s.shadow) text.style.textShadow = "0 2px 6px rgba(0, 0, 0, 0.6)";
        box.inner.appendChild(text);
        break;
      case "image":
        box.inner.appendChild(media("img", safeUrl(s.url), s));
        break;
      case "video":
        var video = media("video", safeUrl(s.url), s);
        video.autoplay = true;
        video.loop = s.loop !== false;
        video.muted = s.muted !== false;
        video.volume = Math.max(0, Math.min(1, (Number(s.volume) || 0) / 100));
        video.playsInline = true;
        box.inner.appendChild(video);
        break;
      case "slideshow":
        var urls = (s.urls || []).map(safeUrl).filter(Boolean);
        if (urls.length === 0) break;
        var index = 0;
        var slides = urls.map(function (url, i) {
          var isVideo = /\.(mp4|webm|mov)(\?|$)/i.test(url);
          var el = media(isVideo ? "video" : "img", url, s);
          if (isVideo) { el.autoplay = true; el.loop = true; el.muted = true; el.playsInline = true; }
          el.classList.add("cuebot-slide");
          if (s.transition === "fade") el.classList.add("fade");
          if (i === 0) el.classList.add("shown");
          box.inner.appendChild(el);
          return el;
        });
        if (slides.length > 1) {
          setInterval(function () {
            slides[index].classList.remove("shown");
            index = (index + 1) % slides.length;
            slides[index].classList.add("shown");
          }, Math.max(1, Number(s.seconds) || 8) * 1000);
        }
        break;
    }
  }

  function buildLayers(stage) {
    (config.layers || []).forEach(function (layer) {
      if (!layer.visible) return;
      var box = layerBox(layer);
      stage.appendChild(box);
      if (layer.type !== "custom") {
        staticLayer(box, layer);
        return;
      }

      var frame = document.createElement("iframe");
      // Scripts on the widget's own origin; no popups, forms or navigating the overlay page.
      frame.setAttribute("sandbox", "allow-scripts allow-same-origin");
      frame.setAttribute("allow", "autoplay");
      frame.setAttribute("title", layer.name || "Widget");
      frame.className = "cuebot-frame";
      box.inner.appendChild(frame);

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
