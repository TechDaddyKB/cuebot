// Cuebot widget editor: a settings form built from the widget's FIELDS, its code panes, your own CSS, a live
// preview of unsaved changes (served from the widget port, like overlays) and Emulate buttons that send sample events
// from each platform to the preview only. The preview speaks the same messages as overlay pages (load, event,
// session, SE_API requests), so widgets behave here as they do in OBS.
(function () {
  "use strict";

  var cfg = window.CUEBOT_EDITOR || {};
  var auth = "editor=" + encodeURIComponent(cfg.editor);
  var api = "/edit/api/" + encodeURIComponent(cfg.overlayId) + "/" + encodeURIComponent(cfg.layerId);
  var PANES = { html: "HTML", css: "CSS", js: "JS", fields: "Fields (JSON)", data: "Data (JSON)", cssOverride: "My CSS" };
  var HINTS = {
    html: "The widget's HTML. {field} and {{field}} are replaced with the field's value.",
    css: "The widget's CSS. {field} placeholders work here too.",
    js: "The widget's JavaScript. It gets onWidgetLoad, onEventReceived and SE_API, as on StreamElements.",
    fields: "The settings schema: field name → {type, label, value, group, ...}. The Settings tab is built from it.",
    data: "Saved setting values (what the Settings tab edits).",
    cssOverride: "Your own CSS, applied after the widget's. Restyle a bought widget here without touching its code; chat messages carry the platform in data.service (\"twitch\", \"kick\", \"rumble\", \"youtube\"). For Cuebot's chat box set Layout to YouTube popout and paste a YouTube chat CSS theme here."
  };

  var state = { html: "", css: "", js: "", fields: "{}", data: "{}", cssOverride: "" };
  var fields = {};
  var data = {};
  var tab = "settings";
  var dirty = false;
  var previewTimer = null;
  var previewDoc = null;
  var frameLoaded = false;

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

  // ---- State ------------------------------------------------------------------------------------------------------

  function valueOf(key) {
    if (Object.prototype.hasOwnProperty.call(data, key)) return data[key];
    return fields[key] ? fields[key].value : undefined;
  }

  function setValue(key, value) {
    data[key] = value;
    state.data = JSON.stringify(data, null, 2);
    if (tab === "data") $("code").value = state.data;
    changed();
  }

  function changed() {
    dirty = true;
    $("save").disabled = false;
    status("Unsaved changes");
    schedulePreview();
  }

  // ---- Settings form ----------------------------------------------------------------------------------------------

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      if (k === "text") node.textContent = attrs[k];
      else if (k in node && typeof attrs[k] !== "string") node[k] = attrs[k];
      else node.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(c); });
    return node;
  }

  function isHex(color) { return /^#[0-9a-f]{6}$/i.test(String(color || "")); }

  function upload(file, done) {
    status("Uploading " + file.name + "…");
    post("/edit/api/assets?" + auth + "&name=" + encodeURIComponent(file.name), file, true)
      .then(function (json) { status("Uploaded " + file.name); done(json.url); })
      .catch(function (e) { status("Upload failed: " + e.message, true); });
  }

  function control(key, f) {
    var type = String(f.type || "text");
    var value = valueOf(key);
    var id = "f-" + key;
    switch (type) {
      case "checkbox":
        var box = el("input", { type: "checkbox", id: id });
        box.checked = value === true || value === "true";
        box.addEventListener("change", function () { setValue(key, box.checked); });
        return el("label", { class: "check" }, [box, document.createTextNode(f.label || key)]);
      case "number":
      case "slider":
        var num = el("input", { type: "number", id: id });
        if (f.min !== undefined) num.min = f.min;
        if (f.max !== undefined) num.max = f.max;
        if (f.step !== undefined) num.step = f.step;
        num.value = value === undefined ? "" : value;
        num.addEventListener("input", function () { if (num.value !== "") setValue(key, Number(num.value)); });
        if (type === "number") return num;
        var range = el("input", { type: "range" });
        range.min = f.min !== undefined ? f.min : 0;
        range.max = f.max !== undefined ? f.max : 100;
        range.step = f.step !== undefined ? f.step : 1;
        range.value = value === undefined ? 0 : value;
        range.addEventListener("input", function () { num.value = range.value; setValue(key, Number(range.value)); });
        num.addEventListener("input", function () { range.value = num.value; });
        num.style.width = "90px";
        return el("div", { class: "row" }, [range, num]);
      case "colorpicker":
        var text = el("input", { type: "text", id: id });
        text.value = value === undefined ? "" : value;
        var picker = el("input", { type: "color" });
        picker.value = isHex(text.value) ? text.value : "#000000";
        picker.addEventListener("input", function () { text.value = picker.value; setValue(key, picker.value); });
        text.addEventListener("input", function () { if (isHex(text.value)) picker.value = text.value; setValue(key, text.value); });
        return el("div", { class: "row" }, [picker, text]);
      case "dropdown":
        var select = el("select", { id: id });
        var options = f.options || {};
        Object.keys(options).forEach(function (v) {
          var option = el("option", { value: v, text: String(options[v]) });
          if (String(value) === v) option.selected = true;
          select.appendChild(option);
        });
        select.addEventListener("change", function () { setValue(key, select.value); });
        return select;
      case "textarea":
        var area = el("textarea", { id: id });
        area.value = value === undefined ? "" : value;
        area.addEventListener("input", function () { setValue(key, area.value); });
        return area;
      case "button":
        var button = el("button", { type: "button", text: f.label || key });
        button.addEventListener("click", function () {
          // As StreamElements' editor sends them: an emulated event whose listener is "widget-button".
          sendToPreview({ type: "event", listener: "event:test", event: { listener: "widget-button", field: key, value: f.value === undefined ? "" : f.value } });
        });
        return button;
      case "image-input":
      case "video-input":
      case "sound-input":
      case "audio-input":
        var multiple = f.multiple === true;
        var url = el(multiple ? "textarea" : "input", { type: "text", id: id, placeholder: multiple ? "One address per line" : "Address or upload" });
        url.value = multiple ? [].concat(value || []).join("\n") : (value || "");
        var commit = function () {
          setValue(key, multiple ? url.value.split("\n").map(function (s) { return s.trim(); }).filter(Boolean) : url.value.trim());
        };
        url.addEventListener("change", commit);
        var file = el("input", { type: "file", accept: type === "image-input" ? "image/*" : type === "video-input" ? "video/*" : "audio/*" });
        file.style.display = "none";
        var pick = el("button", { type: "button", text: "Upload…" });
        pick.addEventListener("click", function () { file.click(); });
        file.addEventListener("change", function () {
          if (!file.files[0]) return;
          upload(file.files[0], function (address) {
            url.value = multiple && url.value.trim() ? url.value.trim() + "\n" + address : address;
            commit();
          });
          file.value = "";
        });
        var parts = [url, pick, file];
        if (type === "image-input" && !multiple && value) parts.push(el("img", { class: "thumb", src: value, alt: "" }));
        return el("div", { class: "row" }, parts);
      default:
        var input = el("input", { type: "text", id: id });
        input.value = value === undefined || value === null ? "" : value;
        if (type === "googleFont" || type === "fontpicker") input.placeholder = "Google Font name, e.g. Poppins";
        input.addEventListener("input", function () { setValue(key, input.value); });
        return input;
    }
  }

  function renderForm() {
    var form = $("form");
    form.textContent = "";
    var keys = Object.keys(fields).filter(function (k) {
      var f = fields[k];
      return f && typeof f === "object" && f.type !== "hidden" && k.indexOf("widget") !== 0;
    });
    if (keys.length === 0) {
      form.appendChild(el("p", { class: "muted", text: "This widget has no settings. Its FIELDS pane is empty." }));
      return;
    }
    var groups = {};
    var order = [];
    keys.forEach(function (k) {
      var g = fields[k].group || "Settings";
      if (!groups[g]) { groups[g] = []; order.push(g); }
      groups[g].push(k);
    });
    order.forEach(function (g, i) {
      var box = el("details", { class: "group" }, [el("summary", { text: g })]);
      box.open = i === 0 || order.length <= 3;
      groups[g].forEach(function (k) {
        var f = fields[k];
        var label = f.type === "checkbox" || f.type === "button" ? null : el("label", { for: "f-" + k, text: f.label || k });
        box.appendChild(el("div", { class: "field" }, [label, control(k, f)]));
      });
      form.appendChild(box);
    });
  }

  // ---- Tabs and code panes ----------------------------------------------------------------------------------------

  function showTab(name) {
    commitCode();
    tab = name;
    document.querySelectorAll(".tabs button").forEach(function (b) { b.classList.toggle("active", b.dataset.tab === name); });
    $("panel-settings").classList.toggle("active", name === "settings");
    $("panel-code").classList.toggle("active", name !== "settings");
    if (name === "settings") {
      renderForm();
      return;
    }
    $("code-hint").textContent = HINTS[name] || "";
    $("code").value = state[name];
    $("code").setAttribute("aria-label", PANES[name]);
  }

  /** Applies the code pane being left: FIELDS and DATA must stay JSON objects. */
  function commitCode() {
    if (tab === "settings") return true;
    var text = $("code").value;
    if (tab === "fields" || tab === "data") {
      var parsed = parseObject(text);
      if (parsed === null) {
        status((tab === "fields" ? "Fields" : "Data") + " is not a JSON object; fix it before leaving this tab.", true);
        return false;
      }
      if (tab === "fields") fields = parsed;
      else data = parsed;
    }
    state[tab] = text;
    return true;
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

  function schedulePreview() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(refreshPreview, 500);
  }

  function refreshPreview() {
    if (!commitCode()) return;
    post(api + "/preview?" + auth, state).then(function (doc) {
      previewDoc = doc;
      frameLoaded = false;
      frame.src = location.protocol + "//" + location.hostname + ":" + cfg.widgetPort + doc.src;
    }).catch(function (e) { status("Preview failed: " + e.message, true); });
  }

  frame.addEventListener("load", function () {
    if (!previewDoc || !frame.src) return;
    frameLoaded = true;
    sendToPreview({
      type: "load",
      widgetDuration: previewDoc.widgetDuration || 0,
      detail: {
        channel: { username: "", apiToken: "", id: "", providerId: "", avatar: "" },
        currency: { code: "USD", name: "U.S. Dollar", symbol: "$" },
        fieldData: previewDoc.fieldData || {},
        recents: previewDoc.recents || [],
        session: { data: previewDoc.session || {}, settings: { autoReset: true, calendar: true, resetOnStart: false } },
        overlay: { isEditorMode: true, muted: false },
        polls: previewDoc.polls || [],
        giveaway: previewDoc.giveaway || null,
        media: previewDoc.media || null,
        emulated: true
      }
    });
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
    chat: "Chat", follow: "Follow", sub: "Sub", resub: "Resub", gift: "Gift subs", cheer: "Cheer", raid: "Raid",
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
      (kinds[select.value] || []).forEach(function (kind) {
        var b = el("button", { type: "button", text: LABELS[kind] || kind });
        b.addEventListener("click", function () { emulate(select.value, kind); });
        buttons.appendChild(b);
      });
    }
    select.addEventListener("change", draw);
    draw();
  }

  function emulate(platform, kind) {
    post("/edit/api/emulate?" + auth, { platform: platform, kind: kind }).then(function (json) {
      (json.messages || []).forEach(function (m) { sendToPreview({ type: "event", listener: m.listener, event: m.event }); });
      status("Sent a " + (NAMES[platform] || platform) + " " + (LABELS[kind] || kind).toLowerCase() + " to the preview");
    }).catch(function (e) { status("Emulate failed: " + e.message, true); });
  }

  // ---- Load and save ----------------------------------------------------------------------------------------------

  function save() {
    if (!commitCode()) return;
    $("save").disabled = true;
    status("Saving…");
    post(api + "?" + auth, state).then(function () {
      dirty = false;
      status("Saved. Overlays using this widget reload now.");
    }).catch(function (e) {
      $("save").disabled = false;
      status("Could not save: " + e.message, true);
    });
  }

  function load() {
    $("widget-name").textContent = cfg.name || "Widget";
    $("overlay-name").textContent = cfg.overlayName ? "on " + cfg.overlayName : "";
    fetch(api + "?" + auth, { cache: "no-store" }).then(function (r) { return r.json(); }).then(function (code) {
      Object.keys(state).forEach(function (k) { state[k] = code[k] || (k === "fields" || k === "data" ? "{}" : ""); });
      fields = parseObject(state.fields) || {};
      data = parseObject(state.data) || {};
      renderForm();
      refreshPreview();
      status("");
    }).catch(function (e) { status("Could not load the widget: " + e.message, true); });
  }

  document.querySelectorAll(".tabs button").forEach(function (b) {
    b.addEventListener("click", function () { showTab(b.dataset.tab); });
  });
  $("code").addEventListener("input", function () {
    if (tab !== "fields" && tab !== "data") state[tab] = $("code").value;
    changed();
  });
  $("code").addEventListener("keydown", function (e) {
    if (e.key !== "Tab") return;
    e.preventDefault();
    var t = e.target;
    var start = t.selectionStart;
    t.setRangeText("  ", start, t.selectionEnd, "end");
    t.dispatchEvent(new Event("input"));
  });
  $("save").addEventListener("click", save);
  $("emulate-clear").addEventListener("click", refreshPreview);
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
