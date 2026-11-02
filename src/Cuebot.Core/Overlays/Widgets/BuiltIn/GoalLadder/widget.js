// Cuebot goal ladder: progress of a points goal with steps ("10: new emote, 20: second emote..."), from the
// "cuebot-subathon" session data (Cuebot's Subathon & Goals page). The bar fills towards the next step; the list
// ticks off steps already reached.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var reachedCount = null;

  function bool(value) { return value === true || value === "true"; }
  function num(value) { return Math.round((Number(value) || 0) * 100) / 100; }

  function pick(data) {
    var all = (data["cuebot-subathon"] && data["cuebot-subathon"].goals) || [];
    var wanted = String(settings.goal || "").trim().toLowerCase();
    if (!wanted) return all[0] || null;
    for (var i = 0; i < all.length; i++) if (String(all[i].name).toLowerCase() === wanted) return all[i];
    return null;
  }

  function render(goal) {
    if (!root) return;
    root.querySelector(".title").textContent = settings.title || (goal ? goal.name : "");
    var fill = root.querySelector(".fill");
    var ol = root.querySelector(".steps");
    ol.textContent = "";
    if (!goal) {
      root.querySelector(".numbers").textContent = "";
      root.querySelector(".next").textContent = "";
      fill.style.width = "0%";
      return;
    }
    var next = goal.next;
    var from = settings.barFrom === "zero" ? 0 : (goal.previous || 0);
    var progress = next ? (goal.points - from) / Math.max(0.0001, next.points - from) : 1;
    fill.style.width = Math.max(0, Math.min(1, progress)) * 100 + "%";
    root.classList.toggle("complete", !next && goal.steps.length > 0);
    root.querySelector(".numbers").textContent = next ? num(goal.points) + " / " + num(next.points) : String(num(goal.points));
    root.querySelector(".next").textContent = next
      ? String(settings.nextText || "").replace("{name}", next.name).replace("{points}", num(next.points))
      : (goal.steps.length ? settings.doneText || "" : "");

    var reached = goal.steps.filter(function (s) { return s.reached; }).length;
    if (reachedCount !== null && reached > reachedCount && bool(settings.flash)) {
      root.classList.remove("flash");
      void root.offsetWidth;   // restart the animation
      root.classList.add("flash");
    }
    reachedCount = reached;

    if (!bool(settings.showSteps)) return;
    var shown = Math.max(1, Number(settings.stepsShown) || 5);
    var start = Math.max(0, Math.min(reached - 1, goal.steps.length - shown));
    goal.steps.slice(start, start + shown).forEach(function (s) {
      var li = document.createElement("li");
      li.className = "step" + (s.reached ? " reached" : "");
      var mark = document.createElement("span");
      mark.className = "mark";
      mark.textContent = s.reached ? "✔" : "○";
      li.appendChild(mark);
      li.appendChild(document.createTextNode(num(s.points) + " – " + s.name));
      ol.appendChild(li);
    });
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("ladder");
    root.classList.toggle("shadow", bool(settings.shadow));
    reachedCount = null;
    render(pick((obj.detail.session && obj.detail.session.data) || {}));
  });

  window.addEventListener("onSessionUpdate", function (obj) { render(pick(obj.detail.session || {})); });
})();
