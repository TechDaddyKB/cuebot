// Cuebot credits: an end-of-stream roll of this stream's subscribers, followers, tippers, cheerers, Kicks senders and
// raiders from every platform, from the stream totals. Built on the StreamElements widget API and its session data.
(function () {
  "use strict";

  var settings = {};
  var data = {};
  var currency = "USD";
  var editor = false;
  var root = null;
  var frame = 0;
  var restartTimer = null;

  var SECTIONS = [
    { title: "subscriberTitle", types: ["subscriber"] },
    { title: "followerTitle", types: ["follower"] },
    { title: "tipTitle", types: ["tip", "superchat", "charityCampaignDonation"], money: true },
    { title: "merchTitle", types: ["merch"], money: true },
    { title: "cheerTitle", types: ["cheer"], unit: " bits" },
    { title: "kicksTitle", types: ["kicks"], unit: " Kicks" },
    { title: "raidTitle", types: ["raid"], unit: " viewers" }
  ];
  var SAMPLES = { subscriber: ["LoyalSub", "GiftGiver"], follower: ["NewFriend", "KickViewer", "RumbleRumbler"], tip: ["Generous"], raid: ["FriendlyStreamer"] };

  function bool(value) { return value === true || value === "true"; }
  function num(value) { return Number(value) || 0; }

  function money(amount) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: currency }).format(amount); }
    catch (err) { return String(amount); }
  }

  /** Everyone in a section, amounts of the same name added up across its types, in first-seen order. */
  function people(section) {
    var order = [];
    var totals = {};
    section.types.forEach(function (t) {
      var users = data[t + "-session-users"] || [];
      if (!users.length && editor && SAMPLES[t]) users = SAMPLES[t].map(function (n) { return { name: n, amount: 5 }; });
      users.forEach(function (u) {
        if (!u || !u.name) return;
        if (!(u.name in totals)) { order.push(u.name); totals[u.name] = 0; }
        totals[u.name] += num(u.amount);
      });
    });
    return order.map(function (name) { return { name: name, amount: totals[name] }; });
  }

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function build() {
    root.textContent = "";
    if (settings.title) root.appendChild(el("h1", "", settings.title));
    var any = false;
    SECTIONS.forEach(function (section) {
      var heading = settings[section.title];
      var list = heading ? people(section) : [];
      if (!list.length) return;
      any = true;
      root.appendChild(el("h2", "", heading));
      list.forEach(function (p) {
        var line = el("div", "name", p.name);
        if (bool(settings.showAmounts) && (section.money || section.unit)) {
          line.appendChild(el("span", "amount", " · " + (section.money ? money(p.amount) : Math.round(p.amount) + section.unit)));
        }
        root.appendChild(line);
      });
    });
    if (!any && settings.emptyText) root.appendChild(el("div", "name", settings.emptyText));
    if (settings.endText) root.appendChild(el("div", "end", settings.endText));
  }

  /** Rolls from just below the bottom edge until everything has gone off the top. */
  function roll() {
    cancelAnimationFrame(frame);
    clearTimeout(restartTimer);
    build();
    var height = root.parentNode.clientHeight;
    var distance = height + root.offsetHeight;
    var speed = Math.max(1, num(settings.speed) || 60);
    var started = null;
    function step(time) {
      if (started === null) started = time;
      var moved = (time - started) / 1000 * speed;
      root.style.transform = "translateY(" + (height - moved) + "px)";
      if (moved < distance) {
        frame = requestAnimationFrame(step);
      } else if (bool(settings.loop)) {
        restartTimer = setTimeout(roll, num(settings.pause) * 1000);   // picks up everyone added meanwhile
      }
    }
    frame = requestAnimationFrame(step);
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    data = (obj.detail.session && obj.detail.session.data) || {};
    currency = (obj.detail.currency && obj.detail.currency.code) || "USD";
    editor = !!(obj.detail.overlay && obj.detail.overlay.isEditorMode);
    root = document.getElementById("credits");
    roll();
  });

  // New names join at the next start; the roll in progress is left alone.
  window.addEventListener("onSessionUpdate", function (obj) { data = obj.detail.session || {}; });

  window.addEventListener("onEventReceived", function (obj) {
    var e = obj.detail.event || {};
    if (obj.detail.listener === "event:test" && e.listener === "widget-button" && e.field === "restart") roll();
  });
})();
