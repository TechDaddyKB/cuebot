// Cuebot seasonal effects: snow, autumn leaves, hearts, confetti, cherry blossoms, bubbles or twinkling stars over the
// overlay, with a burst for every alert if wanted. Drawn on a canvas. Built on the StreamElements widget API.
(function () {
  "use strict";

  var settings = {};
  var canvas = null;
  var ctx = null;
  var particles = [];
  var extra = 0;          // burst particles still to be retired
  var colors = [];
  var last = 0;
  var running = false;

  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }
  function bool(value) { return value === true || value === "true"; }
  function rand(min, max) { return min + Math.random() * (max - min); }
  function pick(list) { return list[Math.floor(Math.random() * list.length)]; }

  var EFFECTS = {
    snow: { size: [2, 6], fall: [30, 80], sway: 25 },
    leaves: { size: [10, 18], fall: [40, 90], sway: 60, spin: true, palette: ["#d9531e", "#e8a33d", "#b5651d", "#c0392b", "#f1c40f"] },
    hearts: { size: [10, 20], fall: [-60, -30], sway: 30 },
    confetti: { size: [6, 12], fall: [80, 160], sway: 40, spin: true },
    sakura: { size: [8, 14], fall: [30, 70], sway: 50, spin: true, palette: ["#ffd1dc", "#ffb7c5", "#ffc0cb", "#fff0f5"] },
    bubbles: { size: [8, 24], fall: [-70, -30], sway: 20 },
    stars: { size: [2, 5], fall: [0, 0], sway: 0, twinkle: true, palette: ["#ffffff", "#fff6c2", "#cfe8ff"] }
  };

  function effect() { return EFFECTS[settings.effect] || EFFECTS.snow; }

  function spawn(top) {
    var e = effect(), scale = num(settings.size, 1), w = canvas.width, h = canvas.height;
    var palette = e.palette || (colors.length ? colors : ["#ffffff"]);
    var up = e.fall[0] < 0;
    return {
      x: rand(0, w),
      y: e.twinkle ? rand(0, h) : top ? (up ? rand(h, h * 1.3) : rand(-h * 0.3, 0)) : rand(0, h),
      r: rand(e.size[0], e.size[1]) * scale,
      vy: rand(e.fall[0], e.fall[1]),
      phase: rand(0, Math.PI * 2),
      sway: rand(0.5, 1.5) * e.sway,
      angle: rand(0, Math.PI * 2),
      spin: e.spin ? rand(-3, 3) : 0,
      color: settings.effect === "snow" ? "#ffffff" : pick(palette)
    };
  }

  function heart(p) {
    var s = p.r / 16;
    ctx.beginPath();
    ctx.moveTo(0, 4 * s);
    ctx.bezierCurveTo(-12 * s, -6 * s, -6 * s, -16 * s, 0, -8 * s);
    ctx.bezierCurveTo(6 * s, -16 * s, 12 * s, -6 * s, 0, 4 * s);
    ctx.fill();
  }

  function draw(p, t) {
    ctx.save();
    ctx.translate(p.x, p.y);
    ctx.rotate(p.angle);
    ctx.fillStyle = p.color;
    ctx.strokeStyle = p.color;
    switch (settings.effect) {
      case "leaves":
        ctx.beginPath();
        ctx.ellipse(0, 0, p.r, p.r * 0.45, 0, 0, Math.PI * 2);
        ctx.fill();
        break;
      case "hearts": heart(p); break;
      case "confetti": ctx.fillRect(-p.r / 2, -p.r / 4, p.r, p.r / 2); break;
      case "sakura":
        for (var i = 0; i < 5; i++) {
          ctx.rotate(Math.PI * 2 / 5);
          ctx.beginPath();
          ctx.ellipse(0, p.r * 0.45, p.r * 0.28, p.r * 0.5, 0, 0, Math.PI * 2);
          ctx.fill();
        }
        break;
      case "bubbles":
        ctx.globalAlpha = 0.5;
        ctx.lineWidth = Math.max(1, p.r / 8);
        ctx.beginPath();
        ctx.arc(0, 0, p.r, 0, Math.PI * 2);
        ctx.stroke();
        break;
      case "stars":
        ctx.globalAlpha = 0.35 + 0.65 * Math.abs(Math.sin(t / 700 + p.phase));
        ctx.beginPath();
        ctx.arc(0, 0, p.r, 0, Math.PI * 2);
        ctx.fill();
        break;
      default:
        ctx.beginPath();
        ctx.arc(0, 0, p.r, 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.restore();
  }

  function frame(t) {
    var dt = Math.min(0.05, (t - last) / 1000 || 0);
    last = t;
    var w = canvas.width, h = canvas.height, speed = num(settings.speed, 1), wind = num(settings.wind, 0.3) * 40;
    ctx.clearRect(0, 0, w, h);
    for (var i = particles.length - 1; i >= 0; i--) {
      var p = particles[i];
      p.phase += dt;
      p.y += p.vy * speed * dt;
      p.x += (wind + Math.sin(p.phase * 1.5) * p.sway) * speed * dt;
      p.angle += p.spin * dt;
      var gone = p.vy >= 0 ? p.y - p.r > h : p.y + p.r < 0;
      if (gone || p.x < -50 || p.x > w + 50) {
        if (extra > 0) { particles.splice(i, 1); extra--; continue; }   // a burst's extras leave
        particles[i] = spawn(true);
        if (p.x < -50) particles[i].x = w + 40;
        if (p.x > w + 50) particles[i].x = -40;
      }
      draw(particles[i] || p, t);
    }
    requestAnimationFrame(frame);
  }

  function resize() {
    canvas.width = canvas.clientWidth || window.innerWidth;
    canvas.height = canvas.clientHeight || window.innerHeight;
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    colors = String(settings.colors || "").split(",").map(function (c) { return c.trim(); }).filter(Boolean);
    canvas = document.getElementById("seasonal");
    ctx = canvas.getContext("2d");
    resize();
    particles = [];
    extra = 0;
    var count = Math.max(1, Math.round(num(settings.density, 80)));
    for (var i = 0; i < count; i++) particles.push(spawn(false));
    last = performance.now();
    if (!running) {   // a second onWidgetLoad (new settings) reuses the loop
      running = true;
      requestAnimationFrame(frame);
    }
  });

  window.addEventListener("resize", function () { if (canvas) resize(); });

  window.addEventListener("onEventReceived", function (obj) {
    if (!canvas || !bool(settings.burst) || !/-latest$/.test(obj.detail.listener || "")) return;
    var n = Math.max(0, Math.round(num(settings.burstSize, 60)));
    for (var i = 0; i < n; i++) particles.push(spawn(true));
    extra += n;
  });
})();
