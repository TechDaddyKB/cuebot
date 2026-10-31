// Cuebot emote rain: emotes from chat on every platform fly across the overlay (rain, fountain, float or bounce),
// a chat command sends a big burst of the emotes in it, and alerts set off bursts of recently seen emotes. Built on
// the StreamElements widget API, so it is edited like any other widget.
(function () {
  "use strict";

  var settings = {};
  var stage = null;
  var sprites = [];
  var pool = [];          // recently seen emote images, for alert bursts
  var lastCommand = 0;
  var running = false;
  var POOL_SIZE = 40;

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" ? n : fallback; }
  function rand(min, max) { return min + Math.random() * (max - min); }

  function emoteUrl(emote) {
    var urls = emote.urls || {};
    return urls["4"] || urls["2"] || urls["1"] || "";
  }

  function remember(url) {
    var at = pool.indexOf(url);
    if (at !== -1) pool.splice(at, 1);
    pool.unshift(url);
    if (pool.length > POOL_SIZE) pool.length = POOL_SIZE;
  }

  function fallbackImages() {
    return String(settings.fallback || "").split(",").map(function (s) { return s.trim(); }).filter(Boolean);
  }

  // ---- Sprites ----------------------------------------------------------------------------------------------------

  function spawn(image) {
    if (!stage || !image) return;
    var max = Math.max(1, num(settings.maxOnScreen, 150));
    if (sprites.length >= max) remove(sprites[0]);
    var el;
    if (/^(https?:)?\/\//.test(image) || image.indexOf("/assets/") === 0) {
      el = document.createElement("img");
      el.src = image;
      el.alt = "";
    } else {
      el = document.createElement("div");
      el.className = "text";
      el.textContent = image;   // an emoji or short text
    }
    el.classList.add("emote");
    stage.appendChild(el);

    var w = stage.clientWidth, h = stage.clientHeight, size = num(settings.size, 64);
    var life = num(settings.duration, 5) * 1000;
    var s = { el: el, born: performance.now(), life: life, size: size, spin: rand(-90, 90) };
    switch (settings.style) {
      case "fountain":
        s.x = rand(w * 0.3, w * 0.7); s.y = h; s.vx = rand(-w * 0.25, w * 0.25); s.vy = -rand(h * 0.9, h * 1.4); s.g = h * 1.2; break;
      case "float":
        s.x = rand(0, w - size); s.y = h; s.vx = rand(-20, 20); s.vy = -rand(h / (life / 1000) * 0.8, h / (life / 1000) * 1.3); s.g = 0; break;
      case "bounce":
        s.x = rand(0, w - size); s.y = rand(0, h - size); s.vx = rand(-400, 400); s.vy = rand(-400, 400); s.g = 0; s.bounce = true; break;
      default:   // rain
        s.x = rand(0, w - size); s.y = -size - rand(0, h * 0.3); s.vx = rand(-30, 30); s.vy = rand(h * 0.15, h * 0.35); s.g = h * 0.35;
    }
    sprites.push(s);
    if (!running) {
      running = true;
      last = performance.now();
      requestAnimationFrame(frame);
    }
  }

  function remove(s) {
    var at = sprites.indexOf(s);
    if (at !== -1) sprites.splice(at, 1);
    if (s.el.parentNode) s.el.parentNode.removeChild(s.el);
  }

  var last = 0;
  function frame(now) {
    var dt = Math.min(0.05, (now - last) / 1000);
    last = now;
    var w = stage.clientWidth, h = stage.clientHeight;
    sprites.slice().forEach(function (s) {
      var age = now - s.born;
      if (age > s.life) { remove(s); return; }
      s.vy += s.g * dt;
      s.x += s.vx * dt;
      s.y += s.vy * dt;
      if (s.bounce) {
        if (s.x < 0 || s.x > w - s.size) { s.vx = -s.vx; s.x = Math.max(0, Math.min(w - s.size, s.x)); }
        if (s.y < 0 || s.y > h - s.size) { s.vy = -s.vy; s.y = Math.max(0, Math.min(h - s.size, s.y)); }
      }
      var fade = Math.min(1, (s.life - age) / 600);
      s.el.style.opacity = String(fade);
      s.el.style.transform = "translate(" + s.x + "px," + s.y + "px) rotate(" + (s.spin * age / 1000) + "deg)";
    });
    if (sprites.length) requestAnimationFrame(frame);
    else running = false;
  }

  /** Sends count emotes picked from images, spread over about a second. */
  function burst(images, count) {
    if (!images.length) return;
    count = Math.min(300, Math.max(0, Math.round(count)));
    for (var i = 0; i < count; i++) {
      (function (image, delay) { setTimeout(function () { spawn(image); }, delay); })(images[i % images.length], i * Math.min(40, 1000 / count));
    }
  }

  function alertImages() { return pool.length ? pool.slice(0, 12) : fallbackImages(); }

  // ---- Chat and alerts --------------------------------------------------------------------------------------------

  function allowed(data) {
    var tags = data.tags || {};
    var mod = tags.mod === "1" || tags.broadcaster === "1";
    if (settings.commandWho === "everyone") return true;
    if (settings.commandWho === "subs") return mod || tags.subscriber === "1" || tags.vip === "1";
    return mod;
  }

  function onMessage(event) {
    var data = event.data || {};
    var urls = (data.emotes || []).map(emoteUrl).filter(Boolean);
    urls.forEach(remember);
    var text = String(data.text || "").trim();
    var command = String(settings.command || "").trim().toLowerCase();
    if (command && text.toLowerCase().split(/\s+/)[0] === command) {
      var now = Date.now();
      if (!allowed(data) || now - lastCommand < num(settings.cooldown, 30) * 1000) return;
      lastCommand = now;
      burst(urls.length ? urls : alertImages(), num(settings.commandCount, 40));
      return;
    }
    if (!bool(settings.chatEmotes)) return;
    urls.slice(0, Math.max(1, num(settings.perMessage, 3))).forEach(function (url, i) {
      setTimeout(function () { spawn(url); }, i * 80);
    });
  }

  function onAlert(type, e) {
    if (!bool(settings.alertBursts)) return;
    var amount = num(e.amount, 1);
    var count = 0;
    switch (type) {
      case "follower": count = num(settings.burstFollow, 10); break;
      case "subscriber": count = num(settings.burstSub, 20) * (bool(e.bulkGifted) ? Math.max(1, amount) : 1); break;
      case "raid": count = num(settings.burstRaid, 60); break;
      case "tip": case "superchat": case "charityCampaignDonation": case "merch": count = amount * num(settings.burstPerDollar, 4); break;
      case "cheer": case "kicks": count = amount / 100 * num(settings.burstPer100Bits, 5); break;
    }
    burst(alertImages(), count);
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    stage = document.getElementById("stage");
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (!stage) return;
    var listener = obj.detail.listener || "";
    var event = obj.detail.event || {};
    if (listener === "message") onMessage(event);
    else if (/-latest$/.test(listener)) onAlert(listener.replace(/-latest$/, ""), event);
    else if (listener === "event:test" && event.listener === "widget-button" && event.field === "testBurst") burst(alertImages(), 30);
  });
})();
