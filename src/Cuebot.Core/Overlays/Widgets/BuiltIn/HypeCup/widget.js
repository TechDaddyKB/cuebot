// Cuebot hype cup: follows, subs, bits, Kicks, tips, Super Chats and raids from every platform drop coloured balls
// into a cup until it is full. The contents are kept in the widget store under the cup's name, so reloading OBS keeps
// them; "Empty the cup" starts over. Built on the StreamElements widget API, so it is edited like any other widget.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var canvas = null;
  var ctx = null;
  var balls = [];        // {x, y, px, py, kind}
  var kinds = [];        // what is in the cup, oldest first (what is saved)
  var running = false;
  var still = 0;

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }
  function key() { return "cuebot-hypecup:" + (String(settings.cupName || "hype-cup").trim() || "hype-cup"); }

  function color(kind) {
    return settings[kind + "Color"] || "#ffffff";
  }

  function radius() { return num(settings.ballSize, 36) / 2; }

  function capacity() { return Math.max(1, num(settings.capacity, 150)); }

  function save() {
    if (window.SE_API) SE_API.store.set(key(), { kinds: kinds });
  }

  function render() {
    root.querySelector(".title").textContent = kinds.length >= capacity() && settings.fullText ? settings.fullText : settings.title || "";
    root.classList.toggle("full", kinds.length >= capacity());
  }

  /** Drops n balls of a kind in from the top, a little apart, up to the per-event cap and what still fits. */
  function add(kind, n, persist) {
    n = Math.min(Math.round(n), num(settings.maxPerEvent, 30), capacity() - kinds.length);
    if (n <= 0) return;
    var w = canvas.width, r = radius();
    for (var i = 0; i < n; i++) {
      var x = r + Math.random() * Math.max(1, w - 2 * r);
      var y = -r - i * r * 1.2;
      balls.push({ x: x, y: y, px: x - (Math.random() - 0.5) * 2, py: y, kind: kind });
      kinds.push(kind);
    }
    if (persist) save();
    render();
    wake();
  }

  function wake() {
    still = 0;
    if (!running) {
      running = true;
      requestAnimationFrame(step);
    }
  }

  /** Verlet integration with wall and ball-to-ball constraints; sleeps once everything has settled. */
  function step() {
    var w = canvas.width, h = canvas.height, r = radius(), gravity = 0.5, moving = 0;
    balls.forEach(function (b) {
      var vx = (b.x - b.px) * 0.99, vy = (b.y - b.py) * 0.99;
      b.px = b.x; b.py = b.y;
      b.x += vx; b.y += vy + gravity;
      moving = Math.max(moving, Math.abs(vx) + Math.abs(vy));
    });
    for (var iteration = 0; iteration < 4; iteration++) {
      for (var i = 0; i < balls.length; i++) {
        var a = balls[i];
        for (var j = i + 1; j < balls.length; j++) {
          var b = balls[j];
          var dx = b.x - a.x, dy = b.y - a.y, d2 = dx * dx + dy * dy, min = r * 2;
          if (d2 > 0 && d2 < min * min) {
            var d = Math.sqrt(d2), push = (min - d) / 2 / d;
            a.x -= dx * push; a.y -= dy * push;
            b.x += dx * push; b.y += dy * push;
          }
        }
        if (a.x < r) a.x = r;
        if (a.x > w - r) a.x = w - r;
        if (a.y > h - r) a.y = h - r;
      }
    }
    draw();
    still = moving < 0.05 ? still + 1 : 0;
    if (still < 60) requestAnimationFrame(step);
    else running = false;
  }

  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    var r = radius();
    balls.forEach(function (b) {
      var gradient = ctx.createRadialGradient(b.x - r * 0.35, b.y - r * 0.35, r * 0.1, b.x, b.y, r);
      gradient.addColorStop(0, "rgba(255,255,255,0.85)");
      gradient.addColorStop(0.25, color(b.kind));
      gradient.addColorStop(1, color(b.kind));
      ctx.beginPath();
      ctx.arc(b.x, b.y, r - 0.5, 0, Math.PI * 2);
      ctx.fillStyle = gradient;
      ctx.fill();
    });
  }

  function resize() {
    var box = canvas.parentNode.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(box.width));
    canvas.height = Math.max(1, Math.round(box.height));
    wake();
  }

  function empty() {
    balls = [];
    kinds = [];
    save();
    render();
    draw();
  }

  function onAlert(type, e) {
    var amount = num(e.amount, 1);
    switch (type) {
      case "follower": add("follow", num(settings.perFollow, 1), true); break;
      case "subscriber": add("sub", num(settings.perSub, 3) * (bool(e.bulkGifted) ? Math.max(1, amount) : 1), true); break;
      case "cheer": case "kicks": add("bits", amount / 100 * num(settings.perHundredBits, 1), true); break;
      case "tip": case "superchat": case "charityCampaignDonation": add("tip", amount * num(settings.perDollar, 1), true); break;
      case "raid": add("raid", num(settings.perRaid, 5), true); break;
    }
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("cup");
    canvas = root.querySelector("canvas");
    ctx = canvas.getContext("2d");
    balls = [];
    kinds = [];
    resize();
    render();
    if (!window.SE_API) return;
    SE_API.store.get(key()).then(function (saved) {
      var restored = saved && Array.isArray(saved.kinds) ? saved.kinds.slice(0, capacity()) : [];
      // Pour the saved contents back in, kind by kind (not saved again: nothing changed).
      var counts = {};
      restored.forEach(function (k) { counts[k] = (counts[k] || 0) + 1; });
      Object.keys(counts).forEach(function (k) {
        for (var left = counts[k]; left > 0; left -= 50) addRaw(k, Math.min(50, left));
      });
      render();
    }).catch(function () { });
  });

  /** Like add, without the per-event cap (restoring). */
  function addRaw(kind, n) {
    var w = canvas.width, r = radius();
    for (var i = 0; i < n && kinds.length < capacity(); i++) {
      var x = r + Math.random() * Math.max(1, w - 2 * r), y = -r - balls.length * r * 0.3;
      balls.push({ x: x, y: y, px: x, py: y, kind: kind });
      kinds.push(kind);
    }
    wake();
  }

  window.addEventListener("resize", function () { if (canvas) resize(); });

  window.addEventListener("onEventReceived", function (obj) {
    if (!root) return;
    var listener = obj.detail.listener || "";
    var e = obj.detail.event || {};
    if (/-latest$/.test(listener)) onAlert(listener.replace(/-latest$/, ""), e);
    else if (listener === "event:test" && e.listener === "widget-button") {
      if (e.field === "empty") empty();
      if (e.field === "testFill") {
        ["follow", "sub", "bits", "tip", "raid"].forEach(function (k) { add(k, 4, true); });
      }
    }
  });
})();
