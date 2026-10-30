// Cuebot giveaway: the prize, how to enter (and what a ticket costs), how many have entered, the latest entrants and
// the time left; on the draw it spins through the entrants' names before landing on the winners. Built on the
// StreamElements widget API; giveaway states arrive as Cuebot's "cuebot-giveaway" event (and onWidgetLoad's giveaway).
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var state = null;
  var shownWinners = "";    // the winners already revealed (so a reload does not spin again)
  var drawnAt = 0;
  var spinning = false;
  var timer = null;

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }
  function $(cls) { return root.querySelector("." + cls); }

  function winnerNames(s) { return (s.winners || []).map(function (w) { return w.name; }).join(", "); }

  function timeLeft() {
    if (!state.endsAt || !bool(settings.showTimer) || state.status !== "open") return "";
    var left = Math.max(0, Math.ceil((Date.parse(state.endsAt) - Date.now()) / 1000));
    return Math.floor(left / 60) + ":" + ("0" + left % 60).slice(-2);
  }

  function how() {
    if (!bool(settings.showHow)) return "";
    if (state.status === "closed") return "Entries are closed";
    var cost = state.ticketCost > 0 ? " (" + state.ticketCost.toLocaleString() + " " + state.currency + " a ticket)" : "";
    var more = state.maxTickets > 1 ? ", up to " + state.maxTickets + " tickets" : "";
    var subs = state.subscribersOnly ? "Subscribers: type " : "Type ";
    return subs + "<b>" + escape(state.command) + "</b> to enter" + escape(cost + more);
  }

  function escape(text) {
    return String(text).replace(/[&<>"]/g, function (c) { return "&#" + c.charCodeAt(0) + ";"; });
  }

  function render() {
    if (!root) return;
    var keep = num(settings.hideAfter, 30) * 1000;
    var visible = state && (state.status === "open" || state.status === "closed" || spinning
      || (state.status === "drawn" && drawnAt && (keep === 0 || Date.now() - drawnAt < keep)));
    root.classList.toggle("hidden", !visible);
    if (!visible) return;
    $("heading").textContent = settings.heading || "";
    $("prize").textContent = state.title;
    $("how").innerHTML = how();
    $("entrants").textContent = state.entrants + (state.entrants === 1 ? " entry" : " entries");
    $("timer").textContent = timeLeft();
    $("recent").textContent = bool(settings.showRecent) && state.recent && state.recent.length ? "Latest: " + state.recent.join(", ") : "";
    root.classList.toggle("drawing", spinning);
    root.classList.toggle("drawn", state.status === "drawn" && !spinning);
    if (state.status === "drawn" && !spinning) {
      $("label").textContent = (state.winners || []).length > 1 ? "Winners" : "Winner";
      $("names").textContent = winnerNames(state) || "Nobody entered";
    }
  }

  /** Flicks through the entrants' names, slowing down, then shows the winners. */
  function spin() {
    var names = state.names && state.names.length ? state.names : [winnerNames(state)];
    var seconds = num(settings.spinSeconds, 4);
    if (seconds <= 0 || names.length < 2) { render(); return; }
    spinning = true;
    $("label").textContent = "Drawing…";
    var started = Date.now();
    (function step() {
      var t = (Date.now() - started) / (seconds * 1000);
      if (t >= 1) { spinning = false; render(); return; }
      $("names").textContent = names[Math.floor(Math.random() * names.length)];
      render();
      setTimeout(step, 50 + 350 * t * t);   // slows down towards the end
    })();
  }

  function update(next, live) {
    var before = state;
    state = next;
    var winners = winnerNames(next);
    if (next && next.status === "drawn" && winners !== shownWinners) {
      shownWinners = winners;
      if (live) {
        drawnAt = Date.now();
        if (before) { spin(); return; }
      }
    }
    render();
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("giveaway");
    var editor = !!(obj.detail.overlay && obj.detail.overlay.isEditorMode);
    var start = obj.detail.giveaway || null;
    if (!start && editor) {
      start = {
        id: "sample", title: "A Steam game of your choice", status: "open", command: "!join", ticketCost: 100, maxTickets: 5,
        currency: "points", subscribersOnly: false, entrants: 42, tickets: 97, endsAt: new Date(Date.now() + 120000).toISOString(),
        recent: ["Ann", "Bob", "Cy"], names: ["Ann", "Bob", "Cy"], winners: []
      };
    }
    shownWinners = start ? winnerNames(start) : "";
    state = null;
    update(start, false);
    clearInterval(timer);
    timer = setInterval(render, 500);
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (obj.detail.listener === "cuebot-giveaway" && obj.detail.event) update(obj.detail.event, true);
  });
})();
