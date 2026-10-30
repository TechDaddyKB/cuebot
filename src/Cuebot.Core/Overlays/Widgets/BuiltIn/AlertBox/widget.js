// Cuebot alert box: one design per alert type, with variations picked by amount, tier, platform, name, gifts or
// chance. Built on the StreamElements widget API: the runtime holds the alert queue (widgetDuration) and each alert
// releases it with SE_API.resumeQueue() once its exit animation has played. Designs live in the hidden "alerts"
// field and are edited in Cuebot's alert designer.
(function () {
  "use strict";

  var settings = {};
  var alerts = { types: {} };
  var root = null;
  var current = null;   // { node, timers[], audio[], done() }
  var muted = false;
  var loadedFonts = {};
  var audioContext = null;

  var PLATFORM_NAMES = { twitch: "Twitch", kick: "Kick", rumble: "Rumble", youtube: "YouTube", streamelements: "StreamElements" };
  var SYSTEM_FONTS = ["", "inherit", "sans-serif", "serif", "monospace", "system-ui", "Arial", "Helvetica", "Verdana",
    "Georgia", "Times New Roman", "Courier New", "Trebuchet MS", "Impact", "Segoe UI"];
  var VIDEO = /\.(mp4|webm|mov|m4v|ogv)(\?|#|$)/i;
  var ENTER_SECONDS = 0.8;
  var EXIT_SECONDS = 0.6;

  function bool(value) { return value === true || value === "true"; }

  function escape(text) {
    return String(text === undefined || text === null ? "" : text).replace(/[&<>"']/g, function (c) { return "&#" + c.charCodeAt(0) + ";"; });
  }

  function money(amount, currency) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: currency || "USD" }).format(amount); }
    catch (err) { return String(amount); }
  }

  function tierName(tier) {
    var t = String(tier || "").toLowerCase();
    if (t === "prime") return "Prime";
    if (/^[123]000$/.test(t)) return "Tier " + t.charAt(0);
    return t;
  }

  // ---- Choosing a design ------------------------------------------------------------------------------------------

  function typeFor(listener, event) {
    if (listener === "cuebot-alert") return event.alertType && alerts.types[event.alertType] ? event.alertType : "custom";
    return listener.replace(/-latest$/, "");
  }

  function paramValue(param, event) {
    switch (param) {
      case "amount": return Number(event.amount) || 0;
      case "tier": return String(event.tier || "").toLowerCase();
      case "platform": return String(event.service || "").toLowerCase();
      case "name": return String(event.name || event.displayName || "").toLowerCase();
      case "gifted": return bool(event.gifted);
      case "bulk": return bool(event.bulkGifted);
      case "currency": return String(event.currency || "").toLowerCase();
      case "message": return String(event.message || "").toLowerCase();
      default: return event[param];
    }
  }

  function matches(variation, event) {
    var actual = paramValue(variation.param, event);
    var value = variation.value;
    switch (variation.op) {
      case "atLeast": return Number(actual) >= Number(value);
      case "exact": return Number(actual) === Number(value);
      case "between": return Number(actual) >= Number(value) && Number(actual) <= Number(variation.value2);
      case "contains": return String(actual).indexOf(String(value || "").toLowerCase()) !== -1;
      case "is":
        if (typeof actual === "boolean") return actual === bool(value);
        // Names and platforms: a comma-separated list matches any of its entries.
        return String(value || "").toLowerCase().split(",").map(function (s) { return s.trim(); }).indexOf(String(actual)) !== -1;
      default: return false;
    }
  }

  function passesChance(variation) {
    var chance = variation.chance === undefined ? 100 : Number(variation.chance);
    return chance >= 100 || Math.random() * 100 < chance;
  }

  /** The design for an alert: a matching variation's (first, or random with randomPick), else the type's own. */
  function choose(type, event) {
    var config = alerts.types[type];
    if (!config || config.enabled === false) return null;
    var candidates = (config.variations || []).filter(function (v) { return v.enabled !== false && matches(v, event); });
    if (bool(alerts.randomPick)) {
      candidates = candidates.filter(passesChance);
      if (candidates.length) return merge(config.design, candidates[Math.floor(Math.random() * candidates.length)].design);
    } else {
      for (var i = 0; i < candidates.length; i++) {
        if (passesChance(candidates[i])) return merge(config.design, candidates[i].design);
      }
    }
    return merge(config.design, null);
  }

  function merge(base, over) {
    var design = {};
    [base || {}, over || {}].forEach(function (source) { Object.keys(source).forEach(function (k) { design[k] = source[k]; }); });
    return design;
  }

  // ---- Rendering --------------------------------------------------------------------------------------------------

  function tokens(event) {
    var amount = event.amount;
    return {
      name: event.name || event.displayName || "",
      amount: amount,
      count: amount,
      currency: event.currency || "",
      formattedAmount: event.formattedAmount || (event.currency ? money(Number(amount) || 0, event.currency) : amount),
      message: event.message || "",
      tier: tierName(event.tier),
      sender: event.sender || "",
      platform: PLATFORM_NAMES[event.service] || event.service || "",
      redemption: event.redemption || ""
    };
  }

  /** The template with each {token} replaced by its value in a highlight span (the text around them is escaped). */
  function headline(template, values) {
    return String(template || "").split(/(\{[a-zA-Z]+\})/).map(function (part) {
      var m = /^\{([a-zA-Z]+)\}$/.exec(part);
      if (m && Object.prototype.hasOwnProperty.call(values, m[1])) return '<span class="hl">' + escape(values[m[1]]) + "</span>";
      return escape(part);
    }).join("");
  }

  function loadFont(font) {
    if (SYSTEM_FONTS.indexOf(font) !== -1 || loadedFonts[font]) return;
    loadedFonts[font] = true;
    var link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = "https://fonts.googleapis.com/css2?family=" + encodeURIComponent(font).replace(/%20/g, "+") + ":wght@400;500;700;800&display=swap";
    document.head.appendChild(link);
  }

  function build(design, event) {
    var values = tokens(event);
    var node = document.createElement("div");
    node.className = "alert layout-" + (design.layout || "above");
    var font = String(design.font || "");
    loadFont(font);
    if (font) node.style.fontFamily = '"' + font.replace(/"/g, "") + '", sans-serif';
    node.style.color = design.color || "#ffffff";
    node.style.setProperty("--highlight", design.highlight || "#ffcc33");

    var media = null;
    if (design.media) {
      var box = document.createElement("div");
      box.className = "media";
      if (VIDEO.test(design.media) || /^data:video\//.test(design.media)) {
        media = document.createElement("video");
        media.src = design.media;
        media.autoplay = true;
        media.playsInline = true;
        media.volume = Math.min(1, Math.max(0, (Number(design.mediaVolume) || 0) / 100));
        media.muted = muted || media.volume === 0;
      } else {
        media = document.createElement("img");
        media.src = design.media;
        media.alt = "";
      }
      box.appendChild(media);
      node.appendChild(box);
    }

    var text = document.createElement("div");
    text.className = "text";
    var head = document.createElement("div");
    head.className = "headline";
    head.style.fontSize = (Number(design.size) || 42) + "px";
    head.innerHTML = headline(design.template, values);
    text.appendChild(head);
    if (bool(design.showMessage) && values.message && String(design.template || "").indexOf("{message}") === -1) {
      var message = document.createElement("div");
      message.className = "message";
      message.style.fontSize = (Number(design.messageSize) || 26) + "px";
      message.textContent = values.message;
      text.appendChild(message);
    }
    node.appendChild(text);
    return { node: node, video: media && media.tagName === "VIDEO" ? media : null };
  }

  // ---- Sound ------------------------------------------------------------------------------------------------------

  /** A short two-note chime, synthesized so the alert box needs no sound file. */
  function chime(volume) {
    var Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    audioContext = audioContext || new Ctx();
    if (audioContext.state === "suspended") audioContext.resume();
    var now = audioContext.currentTime;
    [[880, 0], [1318.5, 0.14]].forEach(function (note) {
      var osc = audioContext.createOscillator();
      var gain = audioContext.createGain();
      osc.type = "sine";
      osc.frequency.value = note[0];
      gain.gain.setValueAtTime(0, now + note[1]);
      gain.gain.linearRampToValueAtTime(0.35 * volume, now + note[1] + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + note[1] + 0.9);
      osc.connect(gain).connect(audioContext.destination);
      osc.start(now + note[1]);
      osc.stop(now + note[1] + 1);
    });
  }

  function playSound(design, playing) {
    var volume = Math.min(1, Math.max(0, (Number(design.volume) || 0) / 100));
    var sound = String(design.sound || "");
    if (muted || volume === 0 || !sound || sound === "none") return;
    if (sound === "chime") { chime(volume); return; }
    var audio = new Audio(sound);
    audio.volume = volume;
    playing.push(audio);
    var started = audio.play();
    if (started && started.catch) started.catch(function () { });
  }

  // ---- Showing an alert -------------------------------------------------------------------------------------------

  function animate(node, name, seconds) {
    node.style.animation = "none";
    void node.offsetWidth;   // restart the animation
    node.style.animation = name && name !== "none" ? name + " " + seconds + "s both" : "none";
  }

  function finish(alert) {
    if (current !== alert) return;
    alert.timers.forEach(clearTimeout);
    alert.audio.forEach(function (a) { a.pause(); });
    if (alert.node.parentNode) alert.node.parentNode.removeChild(alert.node);
    current = null;
    if (alert.release && window.SE_API) SE_API.resumeQueue();
  }

  /** Shows a design for an event; "release" says it came through the queue, which it releases when it is done. */
  function show(design, event, release) {
    if (current) finish(current);
    var built = build(design, event);
    var alert = { node: built.node, timers: [], audio: [], release: release };
    current = alert;
    root.appendChild(built.node);
    animate(built.node, design.enter || "fadeIn", ENTER_SECONDS);
    playSound(design, alert.audio);
    if (built.video) {
      var started = built.video.play();
      if (started && started.catch) started.catch(function () { });
    }
    var seconds = Math.max(1, Number(design.duration) || 8);
    alert.timers.push(setTimeout(function () {
      animate(built.node, design.exit || "fadeOut", EXIT_SECONDS);
      alert.timers.push(setTimeout(function () { finish(alert); }, (design.exit === "none" ? 0 : EXIT_SECONDS) * 1000));
    }, seconds * 1000));
  }

  function onAlert(listener, event) {
    var design = choose(typeFor(listener, event), event);
    if (!design) {
      if (window.SE_API) SE_API.resumeQueue();
      return;
    }
    show(design, event, true);
  }

  // ---- Previews (the alert designer and the editor's test buttons) ------------------------------------------------

  var SAMPLES = {
    follower: { name: "Cuebot", amount: 1 },
    subscriber: { name: "Cuebot", amount: 1, tier: "1000" },
    tip: { name: "Cuebot", amount: 5, currency: "USD", message: "Great stream!" },
    cheer: { name: "Cuebot", amount: 500, message: "Cheer500 Have some bits" },
    raid: { name: "Cuebot", amount: 42 },
    superchat: { name: "Cuebot", amount: 10, currency: "USD", message: "Hello from YouTube!" },
    kicks: { name: "Cuebot", amount: 100 },
    redemption: { name: "Cuebot", amount: 500, redemption: "Hydrate!" },
    jewels: { name: "Cuebot", amount: 25 },
    charityCampaignDonation: { name: "Cuebot", amount: 20, currency: "USD" },
    custom: { name: "Cuebot", message: "A custom alert from an action" }
  };

  /** The sample event changed so that a variation's condition holds (a Kick variation previews a Kick alert). */
  function sampleFor(variation, event) {
    if (!variation) return event;
    var value = variation.value;
    switch (variation.param) {
      case "amount": event.amount = Number(value) || 0; break;
      case "tier": event.tier = String(value); break;
      case "platform": event.service = String(value); break;
      case "name": event.name = String(value || "").split(",")[0].trim() || event.name; break;
      case "gifted": event.gifted = bool(value); if (event.gifted) { event.sender = "Cuebot"; event.name = "A viewer"; } break;
      case "bulk": event.bulkGifted = bool(value); event.amount = 5; break;
      case "currency": event.currency = String(value).toUpperCase(); break;
      case "message": event.message = String(value || event.message); break;
    }
    return event;
  }

  /** Shows a type's design or one of its variations; the designer sends its unsaved designs along. */
  function preview(request) {
    var source = request.alerts && request.alerts.types ? request.alerts : alerts;
    var type = request.alertType || "follower";
    var config = source.types[type];
    if (!config) return;
    var variation = (config.variations || [])[request.variation];
    var event = sampleFor(variation, merge(merge(SAMPLES[type] || SAMPLES.follower, { service: "twitch" }), request.event));
    show(merge(config.design, variation ? variation.design : null), event, false);
  }

  // ---- Events -----------------------------------------------------------------------------------------------------

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    var value = settings.alerts;
    if (typeof value === "string") { try { value = JSON.parse(value); } catch (err) { value = null; } }
    alerts = value && value.types ? value : { types: {} };
    root = document.getElementById("alerts");
    muted = !!(obj.detail.overlay && obj.detail.overlay.muted);
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (!root) return;
    var listener = obj.detail.listener;
    var event = obj.detail.event || {};
    if (listener === "alertService:toggleSound") {
      muted = !!event.muted;
      if (muted && current) current.audio.forEach(function (a) { a.pause(); });
    } else if (listener === "event:skip") {
      if (current) { current.release = false; finish(current); }   // the runtime already released the queue
    } else if (listener === "event:test" && event.listener === "alertbox-preview") {
      preview(event);
    } else if (listener === "event:test" && event.listener === "widget-button" && event.field === "testAlert") {
      preview({ alertType: "follower" });
    } else if (/-latest$/.test(listener) || listener === "cuebot-alert") {
      onAlert(listener, event);
    }
  });
})();
