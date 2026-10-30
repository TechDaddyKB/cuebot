// Cuebot poll and prediction: shows the running poll or prediction from Twitch, YouTube or Cuebot's own chat polls
// (voted on every platform), with live bars, the time left and the winner, then hides a while after it ends. Built on
// the StreamElements widget API; poll states arrive as Cuebot's "cuebot-poll" event (and in onWidgetLoad's polls).
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var states = {};      // "source:kind" -> {state, endedAt (ms, when it ended while this page watched)}
  var timer = null;
  var editor = false;

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }
  function open(state) { return state.status === "active" || state.status === "locked"; }

  function wanted(state) {
    if (settings.source && settings.source !== "any" && state.source !== settings.source) return false;
    if (settings.show && settings.show !== "both" && state.kind !== settings.show) return false;
    return true;
  }

  function remember(state, live) {
    var key = state.source + ":" + state.kind;
    var before = states[key];
    var endedAt = open(state) ? null : before && before.state.id === state.id && before.endedAt ? before.endedAt : live ? Date.now() : 0;
    states[key] = { state: state, endedAt: endedAt, seen: Date.now() };
  }

  /** The newest open poll that fits the settings, else one that ended recently enough to still show. */
  function current() {
    var keep = num(settings.hideAfter, 20) * 1000;
    var best = null;
    Object.keys(states).forEach(function (key) {
      var entry = states[key];
      if (!wanted(entry.state)) return;
      var showing = open(entry.state) || (entry.endedAt && (keep === 0 || Date.now() - entry.endedAt < keep));
      if (!showing) return;
      var rank = (open(entry.state) ? 2e13 : 0) + entry.seen;
      if (!best || rank > best.rank) best = { rank: rank, state: entry.state };
    });
    return best && best.state;
  }

  function timeLeft(state) {
    if (!state.endsAt || !bool(settings.showTimer)) return "";
    var left = Math.max(0, Math.ceil((Date.parse(state.endsAt) - Date.now()) / 1000));
    var m = Math.floor(left / 60), s = left % 60;
    return m + ":" + (s < 10 ? "0" : "") + s;
  }

  function statusText(state) {
    switch (state.status) {
      case "locked": return "Bets locked";
      case "canceled": return "Cancelled";
      case "ended":
        if (state.winner >= 0 && state.choices[state.winner]) return (state.kind === "prediction" ? "Result: " : "Winner: ") + state.choices[state.winner].title;
        return "Ended";
      default: return timeLeft(state);
    }
  }

  function numbers(state, choice) {
    var amount = state.kind === "prediction" ? choice.points.toLocaleString() + (state.currency ? " " + state.currency : "") : String(choice.votes);
    switch (settings.numbers) {
      case "votes": return amount;
      case "percent": return Math.round(choice.percent) + "%";
      case "none": return "";
      default: return amount + " · " + Math.round(choice.percent) + "%";
    }
  }

  function hint(state) {
    if (!bool(settings.showHint) || state.source !== "cuebot" || state.status !== "active" || !state.command) return "";
    return state.kind === "prediction"
      ? "Bet with " + state.command + " 1 500 (outcome, then points or all)"
      : "Vote with " + state.command + " 1, " + state.command + " 2…";
  }

  function render() {
    if (!root) return;
    var state = current();
    if (!state) { root.classList.add("hidden"); return; }
    root.classList.remove("hidden");
    root.classList.toggle("prediction", state.kind === "prediction");
    root.querySelector(".title").textContent = state.title;
    root.querySelector(".status").textContent = statusText(state);
    root.querySelector(".hint").textContent = hint(state);
    var list = root.querySelector(".choices");
    while (list.children.length > state.choices.length) list.removeChild(list.lastChild);
    state.choices.forEach(function (choice, i) {
      var row = list.children[i];
      if (!row) {
        row = document.createElement("div");
        row.innerHTML = '<div class="fill"></div><div class="row"><span class="label"></span><span class="numbers"></span></div>';
        list.appendChild(row);
      }
      row.className = "choice" + (state.winner === i ? " winner" : state.winner >= 0 ? " loser" : "");
      row.querySelector(".fill").style.width = Math.max(0, Math.min(100, choice.percent)) + "%";
      row.querySelector(".label").textContent = (state.source === "cuebot" ? i + 1 + ". " : "") + choice.title;
      row.querySelector(".numbers").textContent = numbers(state, choice);
    });
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("poll");
    editor = !!(obj.detail.overlay && obj.detail.overlay.isEditorMode);
    states = {};
    (obj.detail.polls || []).forEach(function (p) { remember(p, false); });
    if (editor && !current()) {
      remember({
        source: "cuebot", kind: "poll", id: "sample", title: "What should we play next?", status: "active", winner: -1,
        endsAt: new Date(Date.now() + 90000).toISOString(), command: "!vote", currency: "",
        choices: [{ title: "Horror game", votes: 12, points: 0, percent: 48 }, { title: "Racing", votes: 8, points: 0, percent: 32 },
          { title: "Just chatting", votes: 5, points: 0, percent: 20 }]
      }, true);
    }
    clearInterval(timer);
    timer = setInterval(render, 500);
    render();
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (obj.detail.listener !== "cuebot-poll" || !obj.detail.event) return;
    remember(obj.detail.event, true);
    render();
  });
})();
