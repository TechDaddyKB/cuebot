// Cuebot subathon timer: a countdown that stream events add time to (Cuebot's Subathon & Goals page), from the
// "cuebot-subathon" session data. While it runs the page counts down by itself from the end time, so it stays
// smooth and survives reloads; added time pops up as "+5:00".
(function () {
  "use strict";

  var settings = {};
  var timer = null;
  var receivedAt = 0;
  var lastAdded = null;
  var lastId = null;
  var root = null;

  function bool(value) { return value === true || value === "true"; }
  function pad(n) { return (n < 10 ? "0" : "") + n; }

  function format(seconds) {
    var total = Math.max(0, Math.ceil(seconds - 0.0005));
    var h = Math.floor(total / 3600), m = Math.floor(total / 60) % 60, s = total % 60;
    if (bool(settings.showDays) && h >= 24) return Math.floor(h / 24) + "d " + pad(h % 24) + ":" + pad(m) + ":" + pad(s);
    return h + ":" + pad(m) + ":" + pad(s);
  }

  function pick(data) {
    var all = (data["cuebot-subathon"] && data["cuebot-subathon"].timers) || [];
    var wanted = String(settings.timer || "").trim().toLowerCase();
    if (!wanted) return all[0] || null;
    for (var i = 0; i < all.length; i++) if (String(all[i].name).toLowerCase() === wanted) return all[i];
    return null;
  }

  function remaining() {
    if (!timer) return 0;
    if (timer.state === "running" && timer.endsAt) return Math.max(0, (timer.endsAt - Date.now()) / 1000);
    return timer.remaining;
  }

  function popup(seconds) {
    if (!bool(settings.showAdded) || seconds <= 0) return;
    var el = document.createElement("div");
    el.className = "popup";
    el.textContent = "+" + format(seconds);
    root.querySelector(".popups").appendChild(el);
    setTimeout(function () { el.remove(); }, 2300);
  }

  function render() {
    if (!root) return;
    root.querySelector(".title").textContent = settings.title || "";
    var state = timer ? timer.state : "stopped";
    root.classList.toggle("paused", state === "paused");
    root.querySelector(".time").textContent = timer ? (state === "ended" ? (settings.endedText || format(0)) : format(remaining())) : "—";
    root.querySelector(".status").textContent =
      state === "paused" ? (settings.pausedText || "") : state === "stopped" ? (settings.stoppedText || "") : "";
  }

  function update(data) {
    var next = pick(data || {});
    if (next && lastId === next.id && lastAdded !== null && next.added > lastAdded) popup(next.added - lastAdded);
    timer = next;
    lastAdded = next ? next.added : null;
    lastId = next ? next.id : null;
    receivedAt = Date.now();
    render();
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("subathon");
    root.classList.toggle("shadow", bool(settings.shadow));
    lastAdded = null;
    update((obj.detail.session && obj.detail.session.data) || {});
    setInterval(render, 250);
  });

  window.addEventListener("onSessionUpdate", function (obj) { update(obj.detail.session || {}); });
})();
