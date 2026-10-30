// Cuebot countdown: counts down a number of minutes or to a time of day. A minutes countdown keeps its end time in
// the widget store (by timer name), so reloading OBS or opening the overlay elsewhere shows the same timer instead of
// starting over. Built on the StreamElements widget API, so it is edited like any other widget.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var end = null;      // ms since epoch
  var timer = null;
  var wasDone = false;

  function num(value) { return Number(value) || 0; }
  function key() { return "cuebot-countdown:" + (String(settings.timerName || "countdown").trim() || "countdown"); }

  function pad(n) { return (n < 10 ? "0" : "") + n; }

  function format(ms) {
    var total = Math.max(0, Math.ceil(ms / 1000));
    var h = Math.floor(total / 3600);
    var m = Math.floor(total % 3600 / 60);
    var s = total % 60;
    return h > 0 ? h + ":" + pad(m) + ":" + pad(s) : pad(m) + ":" + pad(s);
  }

  /** "20:00" today (tomorrow once more than 12 hours past), or a full "2026-11-01 20:00". */
  function untilTime(text) {
    text = String(text || "").trim();
    var clock = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(text);
    if (clock) {
      var d = new Date();
      d.setHours(num(clock[1]), num(clock[2]), num(clock[3]), 0);
      if (Date.now() - d.getTime() > 12 * 3600 * 1000) d.setDate(d.getDate() + 1);
      return d.getTime();
    }
    var full = Date.parse(text.replace(" ", "T"));
    return isNaN(full) ? null : full;
  }

  function tick() {
    if (!root) return;
    root.querySelector(".title").textContent = settings.title || "";
    var timeEl = root.querySelector(".time");
    if (end === null) { timeEl.textContent = "--:--"; return; }
    var left = end - Date.now();
    var done = left <= 0;
    timeEl.textContent = done && settings.doneText ? settings.doneText : format(left);
    if (done && !wasDone) root.classList.add("done");
    if (!done) root.classList.remove("done");
    wasDone = done;
  }

  function start(fresh) {
    if (settings.mode === "until") {
      end = untilTime(settings.until);
      tick();
      return;
    }
    var minutes = Math.max(0, num(settings.minutes));
    var begin = function () {
      end = Date.now() + minutes * 60 * 1000;
      if (window.SE_API) SE_API.store.set(key(), { end: end, minutes: minutes });
      tick();
    };
    if (fresh || !window.SE_API) { begin(); return; }
    SE_API.store.get(key()).then(function (saved) {
      var stale = num(settings.restartHours) * 3600 * 1000;
      // Keep a running timer, or one that finished recently; a changed length or an old timer starts again.
      if (saved && num(saved.end) > 0 && num(saved.minutes) === minutes && Date.now() - num(saved.end) < stale) {
        end = num(saved.end);
        tick();
      } else {
        begin();
      }
    }).catch(begin);
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("countdown");
    wasDone = false;
    clearInterval(timer);
    timer = setInterval(tick, 250);
    start(false);
  });

  window.addEventListener("onEventReceived", function (obj) {
    var e = obj.detail.event || {};
    if (obj.detail.listener === "event:test" && e.listener === "widget-button" && e.field === "restart") {
      wasDone = false;
      start(true);
    }
  });
})();
