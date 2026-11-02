// Cuebot top spenders: who spent the most this stream, today, this week, this month, this year or all time, on one
// platform or all of them. Tips, Super Chats, rants, bits, Kicks, subscriptions and gift subs are added up in the
// streamer's chosen currency (Cuebot's Top spenders page), from the "cuebot-top-spenders" session data.
(function () {
  "use strict";

  var settings = {};
  var data = {};
  var root = null;
  var leader = null;

  function bool(value) { return value === true || value === "true"; }

  function list() {
    var top = data["cuebot-top-spenders"];
    var period = top && top.periods && top.periods[settings.period || "stream"];
    if (!period) return null;
    var platform = String(settings.platform || "").trim().toLowerCase();
    if (platform === "" || platform === "all" || platform === "all platforms") platform = "all";
    return period[platform] || { total: 0, spenders: [] };
  }

  function render() {
    if (!root) return;
    root.querySelector(".title").textContent = settings.title || "";
    var ol = root.querySelector(".list");
    ol.textContent = "";
    var current = list();
    var max = Math.max(1, Math.min(10, Number(settings.count) || 3));
    var spenders = current ? (current.spenders || []).slice(0, max) : [];
    spenders.forEach(function (s, i) {
      var li = document.createElement("li");
      li.className = "row" + (i === 0 ? " first" : "");
      if (bool(settings.showRank)) {
        var rank = document.createElement("span");
        rank.className = "rank";
        rank.textContent = s.rank + ".";
        li.appendChild(rank);
      }
      var name = document.createElement("span");
      name.className = "name";
      name.textContent = s.name;
      li.appendChild(name);
      if (bool(settings.showPlatform)) {
        var platform = document.createElement("span");
        platform.className = "platform";
        platform.textContent = s.platform;
        li.appendChild(platform);
      }
      if (bool(settings.showAmount)) {
        var amount = document.createElement("span");
        amount.className = "amount";
        amount.textContent = s.formatted;
        li.appendChild(amount);
      }
      ol.appendChild(li);
    });
    root.querySelector(".empty").textContent = spenders.length ? "" : (settings.emptyText || "");
    var first = spenders.length ? spenders[0].platform + "/" + spenders[0].name : null;
    if (leader !== null && first !== null && first !== leader && bool(settings.flash)) {
      root.classList.remove("flash");
      void root.offsetWidth;   // restart the animation
      root.classList.add("flash");
    }
    leader = first;
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    data = (obj.detail.session && obj.detail.session.data) || {};
    root = document.getElementById("spenders");
    root.classList.toggle("shadow", bool(settings.shadow));
    root.classList.toggle("line", settings.layout === "line");
    leader = null;
    render();
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    data = obj.detail.session || {};
    render();
  });
})();
