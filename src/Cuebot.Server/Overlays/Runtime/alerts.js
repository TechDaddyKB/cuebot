// Cuebot alert designer: the alert box's designs, one per alert type plus variations (picked by amount, tier,
// platform, name, gifts or chance), with a live preview of unsaved changes. Designs are saved in the widget's DATA
// ("alerts"), through the same API as the widget editor; the code editor stays one click away.
(function () {
  "use strict";

  var cfg = window.CUEBOT_EDITOR || {};
  var auth = "editor=" + encodeURIComponent(cfg.editor);
  var api = "/edit/api/" + encodeURIComponent(cfg.overlayId) + "/" + encodeURIComponent(cfg.layerId);

  var TYPES = {
    follower: "Follow", subscriber: "Subscriber", tip: "Tip / donation", cheer: "Cheer (bits)", raid: "Raid",
    superchat: "Super Chat", kicks: "Kicks", redemption: "Channel points", jewels: "YouTube Jewels",
    charityCampaignDonation: "Charity donation", custom: "Custom (actions)"
  };
  var TOKENS = ["name", "amount", "formattedAmount", "currency", "message", "tier", "sender", "platform", "redemption"];
  var PARAMS = {
    amount: "Amount (months, bits, viewers, money…)", tier: "Sub tier", platform: "Platform", name: "User name",
    gifted: "Gifted sub", bulk: "Community gift", currency: "Currency", message: "Message"
  };
  var OPS = { atLeast: "at least", exact: "exactly", between: "between", is: "is", contains: "contains" };
  var TIERS = { "1000": "Tier 1", "2000": "Tier 2", "3000": "Tier 3", prime: "Prime" };
  var PLATFORMS = { twitch: "Twitch", kick: "Kick", rumble: "Rumble", youtube: "YouTube", streamelements: "StreamElements" };
  var LAYOUTS = { above: "Image above text", side: "Image beside text", overlay: "Text over image" };
  var ENTER = ["none", "fadeIn", "fadeInUp", "fadeInDown", "fadeInLeft", "fadeInRight", "slideInUp", "slideInDown",
    "slideInLeft", "slideInRight", "zoomIn", "bounceIn"];
  var EXIT = ["none", "fadeOut", "fadeOutUp", "fadeOutDown", "fadeOutLeft", "fadeOutRight", "slideOutUp", "slideOutDown",
    "slideOutLeft", "slideOutRight", "zoomOut", "bounceOut"];
  var FONTS = ["Inter", "Roboto", "Montserrat", "Poppins", "Bebas Neue", "Bangers", "Press Start 2P", "Permanent Marker",
    "Luckiest Guy", "Oswald", "Lobster", "Arial", "Georgia", "Impact"];

  var state = null;     // the widget code from the API
  var data = {};        // its DATA, parsed
  var fields = {};
  var alerts = null;    // data.alerts
  var selected = { type: "follower", variation: -1 };
  var dirty = false;
  var previewDoc = null;
  var frameLoaded = false;
  var previewTimer = null;

  var $ = function (id) { return document.getElementById(id); };
  var frame = $("preview");

  function status(text, isError) {
    var el = $("status");
    el.textContent = text || "";
    el.style.color = isError ? "#d93025" : "";
  }

  function post(url, body, raw) {
    return fetch(url, {
      method: "POST",
      headers: raw ? {} : { "Content-Type": "application/json" },
      body: raw ? body : JSON.stringify(body)
    }).then(function (r) {
      return r.json().then(function (json) {
        if (!r.ok || json.error) throw new Error(json.error || ("HTTP " + r.status));
        return json;
      });
    });
  }

  function parseObject(text) {
    try {
      var value = JSON.parse(text || "{}");
      return value && typeof value === "object" && !Array.isArray(value) ? value : null;
    } catch (e) {
      return null;
    }
  }

  function clone(value) { return JSON.parse(JSON.stringify(value)); }

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      if (k === "text") node.textContent = attrs[k];
      else if (k.slice(0, 2) === "on") node.addEventListener(k.slice(2), attrs[k]);
      else node.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(c); });
    return node;
  }

  function options(select, values, current) {
    var list = Array.isArray(values) ? values.reduce(function (o, v) { o[v] = v; return o; }, {}) : values;
    Object.keys(list).forEach(function (v) {
      var o = el("option", { value: v, text: list[v] });
      if (String(current) === v) o.selected = true;
      select.appendChild(o);
    });
    return select;
  }

  function changed(redrawList) {
    dirty = true;
    $("save").disabled = false;
    if (redrawList) { renderTypes(); renderDesigns(); }
    schedulePreview();
  }

  // ---- What is selected -------------------------------------------------------------------------------------------

  function typeConfig() { return alerts.types[selected.type]; }

  function variation() {
    return selected.variation >= 0 ? (typeConfig().variations || [])[selected.variation] : null;
  }

  /** The design being edited: a variation's (a full design of its own) or the type's. */
  function design() {
    var v = variation();
    return v ? v.design : typeConfig().design;
  }

  function ruleText(v) {
    var value = v.param === "tier" ? (TIERS[v.value] || v.value) : v.param === "platform" ? (PLATFORMS[v.value] || v.value) : v.value;
    var text = (PARAMS[v.param] || v.param).split(" (")[0].toLowerCase() + " " + (OPS[v.op] || v.op) + " " +
      (v.op === "between" ? value + "–" + v.value2 : String(value));
    if (v.param === "gifted" || v.param === "bulk") text = (bool(v.value) ? "" : "not ") + PARAMS[v.param].toLowerCase();
    if (Number(v.chance) < 100) text += ", " + v.chance + "%";
    return text;
  }

  function bool(value) { return value === true || value === "true"; }

  // ---- Lists ------------------------------------------------------------------------------------------------------

  function renderTypes() {
    var list = $("types");
    list.textContent = "";
    Object.keys(TYPES).forEach(function (type) {
      var config = alerts.types[type];
      if (!config) return;
      var box = el("input", {
        type: "checkbox", title: "Show this alert", "aria-label": "Show " + TYPES[type] + " alerts",
        onclick: function (e) { e.stopPropagation(); },
        onchange: function () { config.enabled = box.checked; changed(true); }
      });
      box.checked = config.enabled !== false;
      var item = el("li", {
        role: "option", "aria-selected": String(type === selected.type),
        class: (type === selected.type ? "active" : "") + (config.enabled === false ? " off" : ""),
        onclick: function () { select(type, -1); }
      }, [box, el("span", { text: TYPES[type] })]);
      list.appendChild(item);
    });
    $("random-pick").checked = bool(alerts.randomPick);
  }

  function renderDesigns() {
    var box = $("designs");
    box.textContent = "";
    var config = typeConfig();
    var variations = config.variations || (config.variations = []);
    box.appendChild(el("div", { class: "head" }, [
      el("strong", { text: TYPES[selected.type] + " designs" }),
      el("button", { type: "button", text: "+ Variation", title: "A different design when a condition matches", onclick: addVariation })
    ]));
    box.appendChild(el("div", {
      class: "item" + (selected.variation < 0 ? " active" : ""), onclick: function () { select(selected.type, -1); }
    }, [el("span", { class: "name", text: "Default" }), el("span", { class: "rule", text: "when no variation matches" })]));
    variations.forEach(function (v, i) {
      var enabled = el("input", {
        type: "checkbox", title: "Use this variation", onclick: function (e) { e.stopPropagation(); },
        onchange: function () { v.enabled = enabled.checked; changed(true); }
      });
      enabled.checked = v.enabled !== false;
      function move(delta) {
        return function (e) {
          e.stopPropagation();
          var j = i + delta;
          if (j < 0 || j >= variations.length) return;
          variations.splice(j, 0, variations.splice(i, 1)[0]);
          selected.variation = j;
          changed(true);
        };
      }
      box.appendChild(el("div", {
        class: "item" + (selected.variation === i ? " active" : ""), onclick: function () { select(selected.type, i); }
      }, [
        enabled,
        el("span", { class: "name", text: v.name || "Variation " + (i + 1) }),
        el("span", { class: "rule", text: ruleText(v) }),
        el("button", { type: "button", text: "↑", title: "Check earlier", onclick: move(-1) }),
        el("button", { type: "button", text: "↓", title: "Check later", onclick: move(1) }),
        el("button", {
          type: "button", text: "✕", title: "Delete this variation", onclick: function (e) {
            e.stopPropagation();
            if (!confirm("Delete the variation \"" + (v.name || "Variation " + (i + 1)) + "\"?")) return;
            variations.splice(i, 1);
            selected.variation = -1;
            changed(true);
            renderForm();
          }
        })
      ]));
    });
    if (variations.length) {
      box.appendChild(el("p", { class: "muted small", text: bool(alerts.randomPick)
        ? "A random matching variation is shown (each after its chance)."
        : "The first matching variation, from the top, is shown." }));
    }
  }

  function addVariation() {
    var config = typeConfig();
    var param = selected.type === "subscriber" ? "tier" : selected.type === "follower" ? "platform" : "amount";
    var v = {
      name: "Variation " + (config.variations.length + 1), enabled: true, param: param, op: defaultOp(param),
      value: param === "tier" ? "1000" : param === "platform" ? "twitch" : 10, chance: 100, design: clone(config.design)
    };
    config.variations.push(v);
    select(selected.type, config.variations.length - 1);
    changed(true);
  }

  function defaultOp(param) {
    if (param === "amount") return "atLeast";
    if (param === "message") return "contains";
    return "is";
  }

  function select(type, index) {
    selected = { type: type, variation: index };
    renderTypes();
    renderDesigns();
    renderForm();
    schedulePreview();
  }

  // ---- The design form --------------------------------------------------------------------------------------------

  var fieldIds = 0;

  /** A labelled control; the label names the control itself or the first input inside it. */
  function field(label, input, hint) {
    var target = /^(INPUT|SELECT|TEXTAREA)$/.test(input.tagName) ? input : input.querySelector("input, select, textarea");
    var attrs = { text: label };
    if (target && label) {
      target.id = target.id || "f" + (++fieldIds);
      attrs["for"] = target.id;
    }
    return el("div", { class: "field" }, [label ? el("label", attrs) : null, input, hint ? el("div", { class: "muted small", text: hint }) : null]);
  }

  function text(obj, key, list) {
    var input = el("input", { type: "text", list: list || "" , oninput: function () { obj[key] = input.value; changed(); } });
    input.value = obj[key] === undefined ? "" : obj[key];
    return input;
  }

  function number(obj, key, min, max, step) {
    var input = el("input", {
      type: "number", min: min, max: max, step: step || 1,
      oninput: function () { if (input.value !== "") { obj[key] = Number(input.value); changed(); } }
    });
    input.value = obj[key];
    return input;
  }

  function range(obj, key) {
    var num = el("input", { type: "number", min: 0, max: 100, style: "width:70px" });
    var slider = el("input", { type: "range", min: 0, max: 100 });
    num.value = slider.value = obj[key] === undefined ? 50 : obj[key];
    slider.addEventListener("input", function () { num.value = slider.value; obj[key] = Number(slider.value); changed(); });
    num.addEventListener("input", function () { slider.value = num.value; obj[key] = Number(num.value); changed(); });
    return el("div", { class: "row" }, [slider, num]);
  }

  function check(obj, key, label, after) {
    var box = el("input", { type: "checkbox", onchange: function () { obj[key] = box.checked; changed(true); if (after) after(); } });
    box.checked = bool(obj[key]);
    return el("label", { class: "check" }, [box, document.createTextNode(label)]);
  }

  function choice(obj, key, values, after) {
    var select = el("select", { onchange: function () { obj[key] = select.value; changed(true); if (after) after(); } });
    return options(select, values, obj[key]);
  }

  function color(obj, key) {
    var picker = el("input", { type: "color" });
    var input = el("input", { type: "text" });
    input.value = obj[key] || "";
    if (/^#[0-9a-f]{6}$/i.test(input.value)) picker.value = input.value;
    picker.addEventListener("input", function () { input.value = picker.value; obj[key] = picker.value; changed(); });
    input.addEventListener("input", function () { if (/^#[0-9a-f]{6}$/i.test(input.value)) picker.value = input.value; obj[key] = input.value; changed(); });
    return el("div", { class: "row" }, [picker, input]);
  }

  /** A URL box with an upload button (files go to Cuebot's overlay assets). */
  function file(obj, key, accept, extra) {
    var url = text(obj, key);
    var picker = el("input", { type: "file", accept: accept, style: "display:none" });
    var pick = el("button", { type: "button", text: "Upload…", onclick: function () { picker.click(); } });
    picker.addEventListener("change", function () {
      if (!picker.files || !picker.files[0]) return;
      var f = picker.files[0];
      status("Uploading " + f.name + "…");
      post("/edit/api/assets?" + auth + "&name=" + encodeURIComponent(f.name), f, true).then(function (json) {
        url.value = obj[key] = json.url;
        status("Uploaded " + f.name);
        changed(true);
      }).catch(function (e) { status("Upload failed: " + e.message, true); });
    });
    return el("div", { class: "row" }, [url, pick, picker].concat(extra || []));
  }

  function conditionForm(v) {
    var parts = [el("h3", { text: "When" }), field("Name", text(v, "name"))];
    var param = choice(v, "param", PARAMS, function () {
      v.op = defaultOp(v.param);
      v.value = v.param === "tier" ? "1000" : v.param === "platform" ? "twitch" : v.param === "gifted" || v.param === "bulk" ? true : "";
      renderForm();
    });
    parts.push(field("Check", param));
    var value;
    if (v.param === "amount") {
      var op = choice(v, "op", { atLeast: "at least", exact: "exactly", between: "between" }, renderForm);
      parts.push(field("Is", op));
      value = v.op === "between"
        ? el("div", { class: "pair" }, [number(v, "value", 0, null, "any"), number(v, "value2", 0, null, "any")])
        : number(v, "value", 0, null, "any");
    } else if (v.param === "tier") {
      value = choice(v, "value", TIERS);
    } else if (v.param === "platform") {
      value = choice(v, "value", PLATFORMS);
    } else if (v.param === "gifted" || v.param === "bulk") {
      v.value = bool(v.value);
      var yesNo = el("select", { onchange: function () { v.value = yesNo.value === "true"; changed(true); } });
      value = options(yesNo, { "true": "Yes", "false": "No" }, String(v.value));
    } else {
      value = text(v, "value");
    }
    var hint = v.param === "name" ? "Several names: separate them with commas." : v.param === "amount"
      ? "Months for subs, gift count for community gifts, bits, viewers, or the money amount." : "";
    parts.push(field(v.param === "message" ? "Contains" : "Value", value, hint));
    parts.push(field("Chance (%)", number(v, "chance", 0, 100), "Below 100, the variation is skipped some of the time."));
    return parts;
  }

  function renderForm() {
    var form = $("form");
    form.textContent = "";
    var d = design();
    var v = variation();
    if (v) conditionForm(v).forEach(function (p) { form.appendChild(p); });

    form.appendChild(el("h3", { text: "Text" }));
    var template = text(d, "template");
    var tokens = el("div", { class: "tokens" }, [document.createTextNode("Insert: ")]);
    TOKENS.forEach(function (t) {
      tokens.appendChild(el("code", {
        text: "{" + t + "}", title: "Insert {" + t + "}", onclick: function () {
          var at = template.selectionStart === null ? template.value.length : template.selectionStart;
          template.setRangeText("{" + t + "}", at, template.selectionEnd || at, "end");
          template.dispatchEvent(new Event("input"));
          template.focus();
        }
      }));
      tokens.appendChild(document.createTextNode(" "));
    });
    form.appendChild(field("Headline", template));
    form.appendChild(tokens);
    form.appendChild(field("", check(d, "showMessage", "Show the viewer's message under the headline")));
    form.appendChild(el("div", { class: "pair" }, [field("Font", text(d, "font", "fonts"), "Google Fonts name or a system font"), field("Size (px)", number(d, "size", 8, 300))]));
    form.appendChild(el("div", { class: "pair" }, [field("Text color", color(d, "color")), field("Highlight color", color(d, "highlight"))]));
    form.appendChild(field("Message size (px)", number(d, "messageSize", 8, 200)));

    form.appendChild(el("h3", { text: "Image or video" }));
    form.appendChild(field("File (GIF, PNG, WebP, MP4, WebM)", file(d, "media", "image/*,video/*")));
    form.appendChild(field("Layout", choice(d, "layout", LAYOUTS)));
    form.appendChild(field("Video volume", range(d, "mediaVolume")));

    form.appendChild(el("h3", { text: "Sound" }));
    var custom = d.sound && d.sound !== "chime" && d.sound !== "none";
    var sound = el("select", {
      onchange: function () {
        if (sound.value === "file") { d.sound = ""; renderForm(); return; }
        d.sound = sound.value;
        changed();
        renderForm();
      }
    });
    options(sound, { chime: "Chime (built in)", none: "No sound", file: "A sound file…" }, custom || d.sound === "" ? "file" : d.sound);
    form.appendChild(field("Sound", sound));
    if (custom || d.sound === "") form.appendChild(field("Sound file (MP3, OGG, WAV)", file(d, "sound", "audio/*")));
    form.appendChild(field("Sound volume", range(d, "volume")));

    form.appendChild(el("h3", { text: "Text to speech" }));
    form.appendChild(field("", check(d, "tts", "Read the alert out with Cuebot's speech", renderForm)));
    if (bool(d.tts)) {
      form.appendChild(field("Text to read", text(d, "ttsText"), "Same placeholders as the headline, e.g. {name} says {message}"));
      form.appendChild(field("Voice alias", text(d, "ttsAlias", "voices"), "Empty: the default voice. Filters for bad words and links apply."));
      form.appendChild(el("p", { class: "muted small", text:
        "Alerts join Cuebot's speech queue, so pausing speech or Speak next works on them too. Test this design speaks; the automatic preview does not." }));
    }

    form.appendChild(el("h3", { text: "Timing" }));
    form.appendChild(field("Show for (seconds)", number(d, "duration", 1, 120)));
    form.appendChild(el("div", { class: "pair" }, [field("Enter", choice(d, "enter", ENTER)), field("Exit", choice(d, "exit", EXIT))]));
  }

  // ---- Preview ----------------------------------------------------------------------------------------------------

  function sendToPreview(message) {
    if (frameLoaded && frame.contentWindow) frame.contentWindow.postMessage(message, "*");
  }

  function fit() {
    var wrap = $("stage-wrap");
    var stage = $("stage");
    var w = Number(cfg.width) || 1920;
    var h = Number(cfg.height) || 1080;
    var scale = Math.min(wrap.clientWidth / w, wrap.clientHeight / h, 1);
    stage.style.width = w + "px";
    stage.style.height = h + "px";
    stage.style.transform = "scale(" + scale + ")";
  }

  function codeWithDesigns() {
    data.alerts = alerts;
    var code = clone(state);
    code.data = JSON.stringify(data, null, 2);
    return code;
  }

  function loadPreview() {
    post(api + "/preview?" + auth, codeWithDesigns()).then(function (doc) {
      previewDoc = doc;
      frameLoaded = false;
      frame.src = location.protocol + "//" + location.hostname + ":" + cfg.widgetPort + doc.src;
    }).catch(function (e) { status("Preview failed: " + e.message, true); });
  }

  /** Sends the current designs to the preview (a fresh onWidgetLoad) and shows the selected one; "talk" lets its TTS speak. */
  function play(talk) {
    if (!frameLoaded) return;
    var fieldData = clone(previewDoc.fieldData || {});
    fieldData.alerts = alerts;
    sendToPreview({ type: "load", widgetDuration: previewDoc.widgetDuration || 0, detail: loadDetail(fieldData) });
    sendToPreview({ type: "event", listener: "alertService:toggleSound", event: { muted: $("mute").checked } });
    sendToPreview({
      type: "event", listener: "event:test",
      event: { listener: "alertbox-preview", alertType: selected.type, variation: selected.variation, alerts: alerts, speak: talk === true }
    });
  }

  function schedulePreview() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(function () { play(false); }, 600);
  }

  function loadDetail(fieldData) {
    return {
      channel: { username: "", apiToken: "", id: "", providerId: "", avatar: "" },
      currency: { code: "USD", name: "U.S. Dollar", symbol: "$" },
      fieldData: fieldData,
      recents: [],
      session: { data: {}, settings: { autoReset: true, calendar: true, resetOnStart: false } },
      overlay: { isEditorMode: true, muted: $("mute").checked },
      emulated: true
    };
  }

  frame.addEventListener("load", function () {
    if (!previewDoc || !frame.src) return;
    frameLoaded = true;
    sendToPreview({ type: "load", widgetDuration: previewDoc.widgetDuration || 0, detail: loadDetail(previewDoc.fieldData || {}) });
  });

  // SE_API requests from the preview.
  window.addEventListener("message", function (e) {
    if (e.source !== frame.contentWindow || !e.data || e.data.cuebot !== "request") return;
    var request = e.data;
    post(api + "/request?" + auth, { request: request.request, data: request.data })
      .then(function (json) { sendToPreview({ type: "response", id: request.id, result: json.result }); })
      .catch(function (err) { sendToPreview({ type: "response", id: request.id, error: err.message }); });
  });

  // ---- Emulate ----------------------------------------------------------------------------------------------------

  var LABELS = {
    follow: "Follow", sub: "Sub", resub: "Resub", gift: "Gift subs", cheer: "Cheer", raid: "Raid",
    kicks: "Kicks", tip: "Tip", superchat: "Super Chat"
  };
  var NAMES = { twitch: "Twitch", kick: "Kick", rumble: "Rumble", youtube: "YouTube", donations: "Donations" };

  function renderEmulate() {
    var select = $("emulate-platform");
    var kinds = cfg.emulate || {};
    Object.keys(kinds).forEach(function (p) { select.appendChild(el("option", { value: p, text: NAMES[p] || p })); });
    var buttons = $("emulate-buttons");
    function draw() {
      buttons.textContent = "";
      (kinds[select.value] || []).filter(function (k) { return k !== "chat"; }).forEach(function (kind) {
        buttons.appendChild(el("button", { type: "button", text: LABELS[kind] || kind, onclick: function () { emulate(select.value, kind); } }));
      });
    }
    select.addEventListener("change", draw);
    draw();
  }

  function emulate(platform, kind) {
    if (!frameLoaded) return;
    // The preview must have the unsaved designs before the event arrives.
    var fieldData = clone(previewDoc.fieldData || {});
    fieldData.alerts = alerts;
    sendToPreview({ type: "load", widgetDuration: previewDoc.widgetDuration || 0, detail: loadDetail(fieldData) });
    post("/edit/api/emulate?" + auth, { platform: platform, kind: kind }).then(function (json) {
      (json.messages || []).forEach(function (m) { sendToPreview({ type: "event", listener: m.listener, event: m.event }); });
      status("Sent a " + (NAMES[platform] || platform) + " " + (LABELS[kind] || kind).toLowerCase() + " to the preview");
    }).catch(function (e) { status("Emulate failed: " + e.message, true); });
  }

  // ---- Load and save ----------------------------------------------------------------------------------------------

  function save() {
    $("save").disabled = true;
    status("Saving…");
    var code = codeWithDesigns();
    post(api + "?" + auth, code).then(function () {
      state = code;
      dirty = false;
      status("Saved. Overlays using this alert box reload now.");
    }).catch(function (e) {
      $("save").disabled = false;
      status("Could not save: " + e.message, true);
    });
  }

  function load() {
    $("widget-name").textContent = cfg.name || "Alert box";
    $("overlay-name").textContent = cfg.overlayName ? "on " + cfg.overlayName : "";
    $("code-link").href = location.pathname + "?" + auth + "&code=1";
    fetch(api + "?" + auth, { cache: "no-store" }).then(function (r) { return r.json(); }).then(function (code) {
      state = code;
      fields = parseObject(code.fields) || {};
      data = parseObject(code.data) || {};
      var defaults = fields.alerts && fields.alerts.value;
      alerts = data.alerts && data.alerts.types ? data.alerts : clone(defaults && defaults.types ? defaults : { version: 1, types: {} });
      // Types added in later versions of the alert box get their default design.
      if (defaults && defaults.types) {
        Object.keys(defaults.types).forEach(function (t) { if (!alerts.types[t]) alerts.types[t] = clone(defaults.types[t]); });
      }
      if (!alerts.types[selected.type]) selected.type = Object.keys(alerts.types)[0];
      if (!selected.type) { status("This widget has no alert designs.", true); return; }
      renderTypes();
      renderDesigns();
      renderForm();
      loadPreview();
      status("");
    }).catch(function (e) { status("Could not load the alert box: " + e.message, true); });
  }

  var fontList = el("datalist", { id: "fonts" });
  FONTS.forEach(function (f) { fontList.appendChild(el("option", { value: f })); });
  document.body.appendChild(fontList);
  var voiceList = el("datalist", { id: "voices" });
  (cfg.voices || []).forEach(function (v) { voiceList.appendChild(el("option", { value: v })); });
  document.body.appendChild(voiceList);

  $("random-pick").addEventListener("change", function () { alerts.randomPick = $("random-pick").checked; changed(true); });
  $("mute").addEventListener("change", function () {
    sendToPreview({ type: "event", listener: "alertService:toggleSound", event: { muted: $("mute").checked } });
  });
  $("play").addEventListener("click", function () { play(true); });
  $("save").addEventListener("click", save);
  document.addEventListener("keydown", function (e) {
    if ((e.ctrlKey || e.metaKey) && e.key === "s") {
      e.preventDefault();
      if (!$("save").disabled) save();
    }
  });
  window.addEventListener("beforeunload", function (e) {
    if (dirty) { e.preventDefault(); e.returnValue = ""; }
  });
  window.addEventListener("resize", fit);

  renderEmulate();
  fit();
  load();
})();
