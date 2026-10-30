// Cuebot's StreamElements-compatible widget runtime. It runs inside each widget's sandboxed frame and gives the
// widget what StreamElements gives it: the onWidgetLoad, onEventReceived and onSessionUpdate window events, the
// alert queue (held for the widgetDuration field or until SE_API.resumeQueue()), and SE_API, whose requests go to
// Cuebot through the overlay page. Written for Cuebot from StreamElements' public widget documentation.
(function () {
  "use strict";

  // Delivered at once and never held by the queue (as on StreamElements).
  var NOT_QUEUED = {
    "message": true, "delete-message": true, "delete-messages": true, "bot:counter": true, "event": true,
    "event:test": true, "event:skip": true, "alertService:toggleSound": true, "kvstore:update": true, "widget-button": true,
    "cuebot-poll": true
  };

  var pending = {};
  var sequence = 0;
  var loaded = false;
  var queue = [];
  var holding = false;
  var holdTimer = null;
  var widgetDuration = 0;
  var muted = false;
  var paused = false;   // Cuebot's alert queue pause: the alert playing finishes, the rest wait
  var editorMode = false;

  function request(name, data) {
    return new Promise(function (resolve, reject) {
      var id = "r" + (++sequence);
      pending[id] = { resolve: resolve, reject: reject };
      window.parent.postMessage({ cuebot: "request", id: id, request: name, data: data || {} }, "*");
    });
  }

  function dispatch(name, detail) {
    window.dispatchEvent(new CustomEvent(name, { detail: detail }));
  }

  function deliver(message) {
    dispatch("onEventReceived", { listener: message.listener, event: message.event });
  }

  function release() {
    holding = false;
    clearTimeout(holdTimer);
    pump();
  }

  function pump() {
    while (loaded && !holding && !paused && queue.length > 0) {
      deliver(queue.shift());
      if (widgetDuration > 0) {
        holding = true;
        holdTimer = setTimeout(release, widgetDuration * 1000);
      }
    }
  }

  function whenLoaded(fn) {
    if (document.readyState === "complete") setTimeout(fn, 0);
    else window.addEventListener("load", fn);
  }

  window.SE_API = {
    store: {
      get: function (key) { return request("store_get", { key: key }); },
      set: function (key, value) { return request("store_set", { key: key, value: value }); }
    },
    counters: {
      get: function (name) { return request("counter_get", { name: name }); }
    },
    sanitize: function (message) { return request("sanitize", message || {}); },
    cheerFilter: function (message) { return request("cheer_filter", { message: message }); },
    // Changes a field in StreamElements' editor only; Cuebot's editor saves fields itself.
    setField: function () { },
    getOverlayStatus: function () { return Promise.resolve({ isEditorMode: editorMode, muted: muted }); },
    resumeQueue: function () { release(); },
    sendMessage: function (name, data) { return request(name, data); }
  };

  window.addEventListener("message", function (e) {
    if (e.source !== window.parent) return;
    var m = e.data;
    if (!m || typeof m !== "object") return;
    switch (m.type) {
      case "load":
        widgetDuration = Number(m.widgetDuration) || 0;
        editorMode = !!(m.detail && m.detail.overlay && m.detail.overlay.isEditorMode);
        muted = !!(m.detail && m.detail.overlay && m.detail.overlay.muted);
        paused = !!(m.detail && m.detail.overlay && m.detail.overlay.paused);
        whenLoaded(function () {
          dispatch("onWidgetLoad", m.detail);
          loaded = true;
          pump();
        });
        break;
      case "event":
        if (m.listener === "alertService:toggleSound" && m.event) muted = !!m.event.muted;
        if (m.listener === "event:skip") {
          // The widget ends the alert playing first; only then does the next one start (released the other way
          // round, the widget's skip would end the new alert and leave the queue held).
          if (loaded) deliver(m);
          release();
          break;
        }
        if (NOT_QUEUED[m.listener]) {
          if (loaded) deliver(m);
        } else {
          queue.push(m);
          pump();
        }
        break;
      case "queue":
        paused = !!m.paused;
        pump();
        break;
      case "session":
        if (loaded) dispatch("onSessionUpdate", { session: m.session });
        break;
      case "response":
        var p = pending[m.id];
        if (!p) return;
        delete pending[m.id];
        if (m.error) p.reject(m.error);
        else p.resolve(m.result);
        break;
    }
  });
})();
