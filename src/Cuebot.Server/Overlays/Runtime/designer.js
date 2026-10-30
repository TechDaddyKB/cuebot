// Cuebot overlay designer: the live overlay on a canvas with a box per layer to select, move (with snapping) and
// resize; the layer list (order, visibility, locking, groups); a properties panel (position, look, enter and exit
// animations, and the settings of text, image, video and slideshow layers); undo and redo. Changes save on their own;
// the live overlay (and OBS) reload with them.
(function () {
  "use strict";

  var cfg = window.CUEBOT_DESIGNER || {};
  var auth = "editor=" + encodeURIComponent(cfg.editor);
  var api = "/design/api/" + encodeURIComponent(cfg.overlayId);
  var PRESETS = [
    ["1080p (1920 × 1080)", 1920, 1080], ["1440p (2560 × 1440)", 2560, 1440], ["4K (3840 × 2160)", 3840, 2160],
    ["Vertical (1080 × 1920)", 1080, 1920], ["Custom size", 0, 0]
  ];
  var TYPE_NAMES = { custom: "Widget", text: "Text", image: "Image", video: "Video", slideshow: "Slideshow" };
  var SNAP = 8;

  var overlay = null;
  var elements = [];
  var selected = [];
  var history = [];
  var future = [];
  var saveTimer = null;
  var saving = false;
  var pendingSave = false;
  var scale = 1;
  var userZoom = null;

  var $ = function (id) { return document.getElementById(id); };

  function status(text, isError) {
    var el = $("status");
    el.textContent = text || "";
    el.style.color = isError ? "var(--danger)" : "";
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

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      if (attrs[k] === null || attrs[k] === undefined) return;
      if (k === "text") node.textContent = attrs[k];
      else if (k.indexOf("on") === 0) node.addEventListener(k.slice(2), attrs[k]);
      else node.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(c); });
    return node;
  }

  function layer(id) { return overlay.layers.find(function (l) { return l.id === id; }); }
  function clone(value) { return JSON.parse(JSON.stringify(value)); }

  // ---- State, history and saving --------------------------------------------------------------------------------

  function snapshot() {
    return JSON.stringify({ name: overlay.name, width: overlay.width, height: overlay.height, layers: overlay.layers });
  }

  /** Records the state before a change, so it can be undone. */
  function remember() {
    history.push(snapshot());
    if (history.length > 100) history.shift();
    future = [];
    updateUndo();
  }

  function restore(text) {
    var state = JSON.parse(text);
    overlay.name = state.name;
    overlay.width = state.width;
    overlay.height = state.height;
    overlay.layers = state.layers;
    selected = selected.filter(function (id) { return layer(id); });
    renderAll();
    scheduleSave();
  }

  function undo() {
    if (!history.length) return;
    future.push(snapshot());
    restore(history.pop());
    updateUndo();
  }

  function redo() {
    if (!future.length) return;
    history.push(snapshot());
    restore(future.pop());
    updateUndo();
  }

  function updateUndo() {
    $("undo").disabled = history.length === 0;
    $("redo").disabled = future.length === 0;
  }

  function scheduleSave() {
    status("Saving…");
    clearTimeout(saveTimer);
    saveTimer = setTimeout(save, 500);
  }

  function save() {
    saveTimer = null;
    if (saving) { pendingSave = true; return; }
    saving = true;
    post(api + "?" + auth, { name: overlay.name, width: overlay.width, height: overlay.height, layers: overlay.layers })
      .then(function (state) {
        elements = state.elements || [];
        status("Saved");
      })
      .catch(function (e) { status("Could not save: " + e.message, true); })
      .then(function () {
        saving = false;
        if (pendingSave) { pendingSave = false; save(); }
      });
  }

  function load(state, select) {
    overlay = state.overlay;
    elements = state.elements || [];
    if (select) selected = [select];
    selected = selected.filter(function (id) { return layer(id); });
    if ($("live").getAttribute("src") !== overlay.preview) $("live").src = overlay.preview;
    renderAll();
  }

  // ---- Selection and groups -------------------------------------------------------------------------------------

  /** A layer and the other members of its group. */
  function withGroup(id) {
    var l = layer(id);
    if (!l || !l.groupId) return [id];
    return overlay.layers.filter(function (x) { return x.groupId === l.groupId; }).map(function (x) { return x.id; });
  }

  function select(id, add) {
    var ids = withGroup(id);
    if (add) {
      var all = selected.indexOf(id) !== -1;
      selected = all ? selected.filter(function (s) { return ids.indexOf(s) === -1; }) : selected.concat(ids.filter(function (s) { return selected.indexOf(s) === -1; }));
    } else {
      selected = ids;
    }
    renderAll();
  }

  // ---- Canvas -----------------------------------------------------------------------------------------------------

  function fit() {
    var view = $("viewport");
    var fitScale = Math.min((view.clientWidth - 48) / overlay.width, (view.clientHeight - 48) / overlay.height);
    scale = userZoom || Math.max(0.05, fitScale);
    var canvas = $("canvas");
    canvas.style.width = overlay.width + "px";
    canvas.style.height = overlay.height + "px";
    canvas.style.transform = "scale(" + scale + ")";
    canvas.style.left = Math.max(24, (view.clientWidth - overlay.width * scale) / 2) + "px";
    canvas.style.top = Math.max(24, (view.clientHeight - overlay.height * scale) / 2) + "px";
  }

  function renderBoxes() {
    var boxes = $("boxes");
    boxes.textContent = "";
    overlay.layers.forEach(function (l) {
      var box = el("div", { class: "box" + (selected.indexOf(l.id) !== -1 ? " selected" : "") + (l.locked ? " locked" : "") + (l.visible ? "" : " hidden") });
      box.dataset.id = l.id;
      box.style.left = l.x + "px";
      box.style.top = l.y + "px";
      box.style.width = l.width + "px";
      box.style.height = l.height + "px";
      if (l.rotation) box.style.transform = "rotate(" + l.rotation + "deg)";
      var label = el("span", { class: "label", text: l.name });
      label.style.transform = "scale(" + (1 / scale) + ")";
      label.style.transformOrigin = "0 100%";
      box.appendChild(label);
      if (selected.length === 1 && selected[0] === l.id && !l.locked) {
        ["nw", "n", "ne", "e", "se", "s", "sw", "w"].forEach(function (h) {
          var handle = el("div", { class: "handle" });
          handle.dataset.h = h;
          handle.style.transform = "scale(" + (1 / scale) + ")";
          box.appendChild(handle);
        });
      }
      box.addEventListener("pointerdown", startDrag);
      boxes.appendChild(box);
    });
  }

  // ---- Dragging, resizing and snapping --------------------------------------------------------------------------

  var drag = null;

  function startDrag(e) {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    var id = e.currentTarget.dataset.id;
    var handle = e.target.dataset && e.target.dataset.h;
    if (selected.indexOf(id) === -1 || e.shiftKey) select(id, e.shiftKey);
    var moving = selected.map(layer).filter(function (l) { return l && !l.locked; });
    if (moving.length === 0) return;
    drag = {
      x: e.clientX, y: e.clientY, handle: handle, changed: false, before: snapshot(),
      start: moving.map(function (l) { return { l: l, x: l.x, y: l.y, w: l.width, h: l.height }; })
    };
    window.addEventListener("pointermove", onDrag);
    window.addEventListener("pointerup", endDrag, { once: true });
  }

  /** Edges and centres other layers and the canvas offer for snapping (excluding what moves). */
  function targets() {
    var xs = [0, overlay.width / 2, overlay.width];
    var ys = [0, overlay.height / 2, overlay.height];
    overlay.layers.forEach(function (l) {
      if (selected.indexOf(l.id) !== -1 || !l.visible) return;
      xs.push(l.x, l.x + l.width / 2, l.x + l.width);
      ys.push(l.y, l.y + l.height / 2, l.y + l.height);
    });
    return { xs: xs, ys: ys };
  }

  /** The smallest shift that puts one of the given positions on a target, if any is within reach. */
  function snapOffset(points, candidates) {
    var best = null;
    var reach = SNAP / scale;
    points.forEach(function (p) {
      candidates.forEach(function (c) {
        var d = c - p;
        if (Math.abs(d) <= reach && (best === null || Math.abs(d) < Math.abs(best.d))) best = { d: d, at: c };
      });
    });
    return best;
  }

  function showGuides(gx, gy) {
    document.querySelectorAll(".guide").forEach(function (g) { g.remove(); });
    var canvas = $("boxes");
    if (gx !== null) { var v = el("div", { class: "guide v" }); v.style.left = gx + "px"; v.style.width = (1 / scale) + "px"; canvas.appendChild(v); }
    if (gy !== null) { var h = el("div", { class: "guide h" }); h.style.top = gy + "px"; h.style.height = (1 / scale) + "px"; canvas.appendChild(h); }
  }

  function onDrag(e) {
    if (!drag) return;
    var dx = (e.clientX - drag.x) / scale;
    var dy = (e.clientY - drag.y) / scale;
    if (Math.abs(dx) + Math.abs(dy) < 1 && !drag.changed) return;
    drag.changed = true;
    var snapping = $("snap").checked && !e.altKey;
    var t = snapping ? targets() : null;
    var gx = null, gy = null;

    if (!drag.handle) {
      var minX = Math.min.apply(null, drag.start.map(function (s) { return s.x; })) + dx;
      var minY = Math.min.apply(null, drag.start.map(function (s) { return s.y; })) + dy;
      var maxX = Math.max.apply(null, drag.start.map(function (s) { return s.x + s.w; })) + dx;
      var maxY = Math.max.apply(null, drag.start.map(function (s) { return s.y + s.h; })) + dy;
      if (t) {
        var sx = snapOffset([minX, (minX + maxX) / 2, maxX], t.xs);
        var sy = snapOffset([minY, (minY + maxY) / 2, maxY], t.ys);
        if (sx) { dx += sx.d; gx = sx.at; }
        if (sy) { dy += sy.d; gy = sy.at; }
      }
      drag.start.forEach(function (s) { s.l.x = Math.round(s.x + dx); s.l.y = Math.round(s.y + dy); });
    } else {
      var s = drag.start[0];
      var h = drag.handle;
      var x = s.x, y = s.y, w = s.w, hgt = s.h;
      if (h.indexOf("e") !== -1) w = s.w + dx;
      if (h.indexOf("s") !== -1) hgt = s.h + dy;
      if (h.indexOf("w") !== -1) { x = s.x + dx; w = s.w - dx; }
      if (h.indexOf("n") !== -1) { y = s.y + dy; hgt = s.h - dy; }
      if (e.shiftKey && s.w > 0 && s.h > 0) {
        var ratio = s.w / s.h;
        if (h === "n" || h === "s") w = hgt * ratio; else hgt = w / ratio;
        if (h.indexOf("w") !== -1) x = s.x + s.w - w;
        if (h.indexOf("n") !== -1) y = s.y + s.h - hgt;
      }
      if (t) {
        var edgeX = h.indexOf("w") !== -1 ? x : h.indexOf("e") !== -1 ? x + w : null;
        var edgeY = h.indexOf("n") !== -1 ? y : h.indexOf("s") !== -1 ? y + hgt : null;
        var snapX = edgeX === null ? null : snapOffset([edgeX], t.xs);
        var snapY = edgeY === null ? null : snapOffset([edgeY], t.ys);
        if (snapX) { if (h.indexOf("w") !== -1) { x += snapX.d; w -= snapX.d; } else w += snapX.d; gx = snapX.at; }
        if (snapY) { if (h.indexOf("n") !== -1) { y += snapY.d; hgt -= snapY.d; } else hgt += snapY.d; gy = snapY.at; }
      }
      s.l.x = Math.round(x);
      s.l.y = Math.round(y);
      s.l.width = Math.max(1, Math.round(w));
      s.l.height = Math.max(1, Math.round(hgt));
    }
    renderBoxes();
    showGuides(gx, gy);
    renderProps();
  }

  function endDrag() {
    window.removeEventListener("pointermove", onDrag);
    showGuides(null, null);
    if (drag && drag.changed) {
      history.push(drag.before);
      future = [];
      updateUndo();
      scheduleSave();
    }
    drag = null;
  }

  // ---- Layer list -------------------------------------------------------------------------------------------------

  function renderList() {
    var list = $("layer-list");
    list.textContent = "";
    overlay.layers.slice().reverse().forEach(function (l) {
      var item = el("li", { class: (selected.indexOf(l.id) !== -1 ? "selected" : "") + (l.visible ? "" : " hidden"), role: "option" }, [
        el("button", {
          title: l.visible ? "Hide" : "Show", "aria-label": l.visible ? "Hide " + l.name : "Show " + l.name, text: l.visible ? "👁" : "◌",
          onclick: function (e) { e.stopPropagation(); change(function () { l.visible = !l.visible; }); }
        }),
        el("span", { class: "lname", text: l.name }),
        l.groupId ? el("span", { class: "grouped", text: "group" }) : null,
        el("span", { class: "ltype", text: TYPE_NAMES[l.type] || l.type }),
        el("button", {
          title: l.locked ? "Unlock" : "Lock", "aria-label": (l.locked ? "Unlock " : "Lock ") + l.name, text: l.locked ? "🔒" : "🔓",
          onclick: function (e) { e.stopPropagation(); change(function () { l.locked = !l.locked; }); }
        })
      ]);
      item.setAttribute("aria-selected", selected.indexOf(l.id) !== -1 ? "true" : "false");
      item.addEventListener("click", function (e) { select(l.id, e.shiftKey || e.ctrlKey || e.metaKey); });
      list.appendChild(item);
    });
    var one = selected.length > 0;
    $("raise").disabled = !one;
    $("lower").disabled = !one;
    $("duplicate").disabled = !one;
    $("delete").disabled = !one;
    $("group").disabled = selected.length < 2;
    $("ungroup").disabled = !selected.some(function (id) { var l = layer(id); return l && l.groupId; });
  }

  /** Applies an edit to the layers, with undo and saving. */
  function change(edit) {
    remember();
    edit();
    renderAll();
    scheduleSave();
  }

  function move(direction) {
    change(function () {
      var order = overlay.layers;
      var ids = selected.slice();
      var indexes = ids.map(function (id) { return order.findIndex(function (l) { return l.id === id; }); }).sort(function (a, b) { return direction > 0 ? b - a : a - b; });
      indexes.forEach(function (i) {
        var j = i + direction;
        if (j < 0 || j >= order.length || ids.indexOf(order[j].id) !== -1) return;
        var tmp = order[i]; order[i] = order[j]; order[j] = tmp;
      });
    });
  }

  function newId() {
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, function (c) {
      var r = crypto.getRandomValues(new Uint8Array(1))[0] % 16;
      return (c === "x" ? r : (r & 0x3) | 0x8).toString(16);
    });
  }

  function duplicate() {
    if (!selected.length) return;
    change(function () {
      var copies = selected.map(layer).filter(Boolean).map(function (l) {
        var copy = clone(l);
        copy.id = newId();
        copy.name = l.name + " copy";
        copy.x += 20;
        copy.y += 20;
        copy.groupId = null;
        overlay.layers.push(copy);
        return copy.id;
      });
      selected = copies;
    });
  }

  function remove() {
    if (!selected.length) return;
    change(function () {
      overlay.layers = overlay.layers.filter(function (l) { return selected.indexOf(l.id) === -1; });
      selected = [];
    });
  }

  function group() {
    if (selected.length < 2) return;
    var id = newId();
    change(function () { selected.forEach(function (s) { layer(s).groupId = id; }); });
  }

  function ungroup() {
    change(function () { selected.forEach(function (s) { var l = layer(s); if (l) l.groupId = null; }); });
  }

  // ---- Properties -------------------------------------------------------------------------------------------------

  function row(label, control) {
    return el("div", { class: "row" }, [el("label", { text: label }), control]);
  }

  /** A number input bound to a layer property (applied on change, with undo). */
  function num(l, key, attrs, onSet) {
    var input = el("input", Object.assign({ type: "number" }, attrs || {}));
    input.value = l[key];
    input.addEventListener("change", function () {
      var value = Number(input.value);
      if (isNaN(value)) return;
      change(function () { (onSet || function (v) { l[key] = v; })(value); });
    });
    return input;
  }

  function settingInput(l, key, type, attrs) {
    var s = l.settings = l.settings || {};
    var input;
    if (type === "textarea") input = el("textarea");
    else if (type === "select") {
      input = el("select");
      Object.keys(attrs.options).forEach(function (v) {
        var o = el("option", { value: v, text: attrs.options[v] });
        if (String(s[key]) === v) o.selected = true;
        input.appendChild(o);
      });
    } else input = el("input", Object.assign({ type: type }, attrs || {}));
    if (type === "checkbox") input.checked = s[key] === true;
    else if (type !== "select") input.value = s[key] === undefined || s[key] === null ? "" : s[key];
    input.addEventListener("change", function () {
      var value = type === "checkbox" ? input.checked : type === "number" ? Number(input.value) : input.value;
      change(function () { s[key] = value; });
    });
    return input;
  }

  function colorSetting(l, key) {
    var s = l.settings = l.settings || {};
    var text = settingInput(l, key, "text");
    var picker = el("input", { type: "color" });
    picker.value = /^#[0-9a-f]{6}$/i.test(s[key] || "") ? s[key] : "#ffffff";
    picker.addEventListener("change", function () { change(function () { s[key] = picker.value; }); });
    return el("div", { class: "inline" }, [picker, text]);
  }

  function upload(accept, done) {
    var input = $("upload-input");
    input.accept = accept;
    input.onchange = function () {
      var file = input.files[0];
      input.value = "";
      if (!file) return;
      status("Uploading " + file.name + "…");
      post("/edit/api/assets?" + auth + "&name=" + encodeURIComponent(file.name), file, true)
        .then(function (json) { status("Uploaded " + file.name); done(json.url); })
        .catch(function (e) { status("Upload failed: " + e.message, true); });
    };
    input.click();
  }

  function mediaSetting(l, accept) {
    var s = l.settings = l.settings || {};
    var text = settingInput(l, "url", "text", { placeholder: "Address, or upload a file" });
    var button = el("button", { text: "Upload…", onclick: function () { upload(accept, function (url) { change(function () { s.url = url; }); }); } });
    return el("div", { class: "inline" }, [text, button]);
  }

  var FITS = { contain: "Fit inside", cover: "Fill (crop)", fill: "Stretch" };

  function staticSettings(l) {
    var s = l.settings = l.settings || {};
    var parts = [];
    switch (l.type) {
      case "text":
        parts.push(row("Text", settingInput(l, "text", "textarea")));
        parts.push(row("Font", settingInput(l, "font", "text", { placeholder: "Google Font, e.g. Poppins" })));
        parts.push(row("Size", settingInput(l, "size", "number", { min: 6, max: 400 })));
        parts.push(row("Colour", colorSetting(l, "color")));
        parts.push(row("Weight", settingInput(l, "weight", "select", { options: { "400": "Regular", "600": "Semibold", "700": "Bold", "900": "Black" } })));
        parts.push(row("Align", settingInput(l, "align", "select", { options: { left: "Left", center: "Centre", right: "Right" } })));
        parts.push(row("Shadow", settingInput(l, "shadow", "checkbox")));
        parts.push(row("Background", settingInput(l, "background", "text", { placeholder: "e.g. rgba(0,0,0,0.5)" })));
        parts.push(row("Padding", settingInput(l, "padding", "number", { min: 0, max: 200 })));
        break;
      case "image":
        parts.push(row("Image", mediaSetting(l, "image/*")));
        parts.push(row("Fit", settingInput(l, "fit", "select", { options: FITS })));
        break;
      case "video":
        parts.push(row("Video", mediaSetting(l, "video/*")));
        parts.push(row("Fit", settingInput(l, "fit", "select", { options: FITS })));
        parts.push(row("Loop", settingInput(l, "loop", "checkbox")));
        parts.push(row("Muted", settingInput(l, "muted", "checkbox")));
        parts.push(row("Volume", settingInput(l, "volume", "number", { min: 0, max: 100 })));
        break;
      case "slideshow":
        var urls = el("textarea", { placeholder: "One image or video address per line" });
        urls.value = (s.urls || []).join("\n");
        urls.addEventListener("change", function () {
          change(function () { s.urls = urls.value.split("\n").map(function (u) { return u.trim(); }).filter(Boolean); });
        });
        parts.push(row("Slides", urls));
        parts.push(el("button", {
          class: "wide", text: "Upload a slide…",
          onclick: function () { upload("image/*,video/*", function (url) { change(function () { s.urls = (s.urls || []).concat([url]); }); }); }
        }));
        parts.push(row("Seconds each", settingInput(l, "seconds", "number", { min: 1, max: 600 })));
        parts.push(row("Fit", settingInput(l, "fit", "select", { options: FITS })));
        parts.push(row("Transition", settingInput(l, "transition", "select", { options: { fade: "Fade", none: "None" } })));
        break;
      case "custom":
        parts.push(el("p", { class: "muted", text: "A widget from the library: other overlays can use the same one. Its settings, code and test events are in the widget editor." }));
        parts.push(el("button", {
          class: "wide primary", text: "Edit widget…",
          onclick: function () { window.open("/edit/" + encodeURIComponent(cfg.overlayId) + "/" + encodeURIComponent(l.id) + "?" + auth, "_blank", "noopener"); }
        }));
        break;
    }
    if (l.type !== "custom") {
      parts.push(el("button", {
        class: "wide", text: "Save to library", title: "Keep this " + (TYPE_NAMES[l.type] || "layer").toLowerCase() + " as a template to add to any overlay",
        onclick: function () {
          flushThen(function () {
            post(api + "/save-to-library?" + auth, { layerId: l.id }).then(function (json) {
              elements = json.state.elements || [];
              status("Saved \"" + l.name + "\" to the library");
            }).catch(function (e) { status(e.message, true); });
          });
        }
      }));
    }
    return el("div", { class: "section" }, parts);
  }

  function animation(l, key, names) {
    var a = l[key] = l[key] || { name: "none", duration: 1 };
    var select = el("select");
    names.forEach(function (n) {
      var o = el("option", { value: n, text: n === "none" ? "None" : n });
      if (a.name === n) o.selected = true;
      select.appendChild(o);
    });
    select.addEventListener("change", function () { change(function () { a.name = select.value; }); });
    var duration = el("input", { type: "number", min: 0.1, max: 10, step: 0.1, title: "Seconds" });
    duration.value = a.duration;
    duration.addEventListener("change", function () { change(function () { a.duration = Number(duration.value) || 1; }); });
    return el("div", { class: "pair" }, [select, duration]);
  }

  function renderProps() {
    var props = $("props");
    props.textContent = "";
    if (selected.length !== 1) {
      props.appendChild(el("p", { class: "muted", text: selected.length ? selected.length + " layers selected. Drag them together, or group them." : "Select a layer to change it." }));
      return;
    }
    var l = layer(selected[0]);
    if (!l) return;
    var name = el("input", { type: "text" });
    name.value = l.name;
    name.addEventListener("change", function () { change(function () { l.name = name.value.trim() || l.name; }); });
    props.appendChild(row("Name", name));
    props.appendChild(row("Position", el("div", { class: "pair" }, [num(l, "x", { title: "X" }), num(l, "y", { title: "Y" })])));
    props.appendChild(row("Size", el("div", { class: "pair" }, [num(l, "width", { min: 1, title: "Width" }), num(l, "height", { min: 1, title: "Height" })])));
    var opacity = el("input", { type: "range", min: 0, max: 100 });
    opacity.value = Math.round(l.opacity * 100);
    opacity.addEventListener("change", function () { change(function () { l.opacity = Number(opacity.value) / 100; }); });
    props.appendChild(row("Opacity", opacity));
    props.appendChild(row("Rotation", num(l, "rotation", { min: -360, max: 360 })));
    props.appendChild(row("Enter", animation(l, "enter", cfg.enterAnimations || ["none"])));
    props.appendChild(row("Exit", animation(l, "exit", cfg.exitAnimations || ["none"])));
    props.appendChild(el("button", {
      class: "wide", text: "Centre on the canvas",
      onclick: function () { change(function () { l.x = Math.round((overlay.width - l.width) / 2); l.y = Math.round((overlay.height - l.height) / 2); }); }
    }));
    props.appendChild(el("button", {
      class: "wide", text: "Fill the canvas",
      onclick: function () { change(function () { l.x = 0; l.y = 0; l.width = overlay.width; l.height = overlay.height; }); }
    }));
    props.appendChild(staticSettings(l));
  }

  // ---- Header -----------------------------------------------------------------------------------------------------

  function renderHeader() {
    $("overlay-name").value = overlay.name;
    var preset = $("canvas-preset");
    if (!preset.options.length) PRESETS.forEach(function (p, i) { preset.appendChild(el("option", { value: String(i), text: p[0] })); });
    var match = PRESETS.findIndex(function (p) { return p[1] === overlay.width && p[2] === overlay.height; });
    preset.value = String(match === -1 ? PRESETS.length - 1 : match);
    $("canvas-width").value = overlay.width;
    $("canvas-height").value = overlay.height;
  }

  function renderAll() {
    renderHeader();
    fit();
    renderBoxes();
    renderList();
    renderProps();
  }

  function setCanvas(w, h) {
    if (!(w >= 16 && h >= 16)) return;
    change(function () { overlay.width = Math.min(7680, Math.round(w)); overlay.height = Math.min(7680, Math.round(h)); });
    status("Canvas changed: set your OBS browser source to " + overlay.width + " × " + overlay.height);
  }

  // ---- Adding layers ----------------------------------------------------------------------------------------------

  function add(type, elementId) {
    flushThen(function () {
      post(api + "/add?" + auth, { type: type, elementId: elementId }).then(function (json) {
        remember();
        load(json.state, json.layer);
        status("Added");
      }).catch(function (e) { status("Could not add: " + e.message, true); });
    });
  }

  /** Adds on the server see the saved layers, so unsaved edits are saved first. */
  function flushThen(fn) {
    if (!saveTimer && !saving) { fn(); return; }
    clearTimeout(saveTimer);
    saveTimer = null;
    post(api + "?" + auth, { name: overlay.name, width: overlay.width, height: overlay.height, layers: overlay.layers })
      .then(fn).catch(function (e) { status("Could not save: " + e.message, true); });
  }

  /** Library upkeep; the reply carries this overlay's state, so the canvas and the dialog stay in step. */
  function libraryAction(element, action, body) {
    return flushThenPromise().then(function () {
      return post("/design/api/library/" + encodeURIComponent(element.id) + "/" + action + "?" + auth + "&overlay=" + encodeURIComponent(cfg.overlayId), body || {});
    }).then(function (state) {
      load(state);
      showLibrary();
    }).catch(function (e) { status(e.message, true); refresh(); });
  }

  function refresh() {
    fetch(api + "?" + auth, { cache: "no-store" }).then(function (r) { return r.json(); }).then(function (state) { load(state); showLibrary(); });
  }

  function flushThenPromise() {
    return new Promise(function (resolve) { flushThen(resolve); });
  }

  function showLibrary() {
    var list = $("library-list");
    list.textContent = "";
    if (!elements.length) {
      list.appendChild(el("li", { class: "muted", text: "The library is empty. Widgets you add are kept here, and text, image, video and slideshow layers can be saved to it from their properties." }));
    }
    elements.forEach(function (e) {
      var used = e.usedBy || [];
      var shared = e.type === "custom";
      var info = shared
        ? (used.length ? "Widget · on " + used.join(", ") : "Widget · not on any overlay")
        : (TYPE_NAMES[e.type] || e.type) + " template";
      list.appendChild(el("li", {}, [
        el("div", { class: "lib-info" }, [el("strong", { text: e.name }), el("div", { class: "muted small", text: info })]),
        el("div", { class: "lib-actions" }, [
          el("button", { class: "primary", text: "Add", onclick: function () { $("library").close(); add("element", e.id); } }),
          el("button", {
            text: "Rename", onclick: function () {
              var name = prompt("New name for " + e.name, e.name);
              if (name && name.trim()) libraryAction(e, "rename", { name: name.trim() });
            }
          }),
          el("button", { text: "Duplicate", title: shared ? "A separate copy of the widget, with its own settings and code" : "Copy the template", onclick: function () { libraryAction(e, "duplicate"); } }),
          el("button", {
            class: "danger", text: "Delete", title: used.length ? "Remove it from " + used.join(", ") + " first" : "Delete from the library",
            disabled: used.length ? "disabled" : null,
            onclick: function () { if (confirm("Delete " + e.name + " from the library?")) libraryAction(e, "delete"); }
          })
        ])
      ]));
    });
    if (!$("library").open) $("library").showModal();
  }

  // ---- Wiring -----------------------------------------------------------------------------------------------------

  $("add-button").addEventListener("click", function (e) {
    e.stopPropagation();
    var menu = $("add-menu");
    menu.hidden = !menu.hidden;
    $("add-button").setAttribute("aria-expanded", String(!menu.hidden));
  });
  document.addEventListener("click", function () { $("add-menu").hidden = true; $("add-button").setAttribute("aria-expanded", "false"); });
  document.querySelectorAll("#add-menu [data-add]").forEach(function (b) {
    b.addEventListener("click", function () {
      var what = b.dataset.add;
      if (what === "zip") $("zip-input").click();
      else if (what === "library") showLibrary();
      else add(what);
    });
  });
  $("zip-input").addEventListener("change", function () {
    var file = $("zip-input").files[0];
    $("zip-input").value = "";
    if (!file) return;
    status("Importing " + file.name + "…");
    flushThen(function () {
      post(api + "/import?" + auth + "&name=" + encodeURIComponent(file.name), file, true).then(function (json) {
        remember();
        load(json.state, json.layer);
        status("Added " + file.name);
      }).catch(function (e) { status("Could not add the widget: " + e.message, true); });
    });
  });

  $("overlay-name").addEventListener("change", function () {
    var value = $("overlay-name").value.trim();
    if (value) change(function () { overlay.name = value; });
  });
  $("canvas-preset").addEventListener("change", function () {
    var p = PRESETS[Number($("canvas-preset").value)];
    if (p[1]) setCanvas(p[1], p[2]);
  });
  $("canvas-width").addEventListener("change", function () { setCanvas(Number($("canvas-width").value), overlay.height); });
  $("canvas-height").addEventListener("change", function () { setCanvas(overlay.width, Number($("canvas-height").value)); });

  $("undo").addEventListener("click", undo);
  $("redo").addEventListener("click", redo);
  $("raise").addEventListener("click", function () { move(1); });
  $("lower").addEventListener("click", function () { move(-1); });
  $("duplicate").addEventListener("click", duplicate);
  $("delete").addEventListener("click", remove);
  $("group").addEventListener("click", group);
  $("ungroup").addEventListener("click", ungroup);
  $("zoom-in").addEventListener("click", function () { userZoom = Math.min(4, scale * 1.25); renderAll(); });
  $("zoom-out").addEventListener("click", function () { userZoom = Math.max(0.05, scale / 1.25); renderAll(); });
  $("zoom-fit").addEventListener("click", function () { userZoom = null; renderAll(); });
  $("viewport").addEventListener("pointerdown", function (e) {
    if (e.target.id === "viewport" || e.target.id === "boxes" || e.target.id === "canvas") { selected = []; renderAll(); }
  });
  window.addEventListener("resize", function () { fit(); renderBoxes(); });

  document.addEventListener("keydown", function (e) {
    var typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement && document.activeElement.tagName);
    var mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === "z" && !e.shiftKey && !typing) { e.preventDefault(); undo(); return; }
    if (mod && (e.key.toLowerCase() === "y" || (e.key.toLowerCase() === "z" && e.shiftKey)) && !typing) { e.preventDefault(); redo(); return; }
    if (typing) return;
    if (mod && e.key.toLowerCase() === "d") { e.preventDefault(); duplicate(); return; }
    if (mod && e.key.toLowerCase() === "g") { e.preventDefault(); if (e.shiftKey) ungroup(); else group(); return; }
    if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); remove(); return; }
    var step = e.shiftKey ? 10 : 1;
    var nudge = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key];
    if (nudge && selected.length) {
      e.preventDefault();
      change(function () {
        selected.map(layer).forEach(function (l) { if (l && !l.locked) { l.x += nudge[0]; l.y += nudge[1]; } });
      });
    }
  });

  window.addEventListener("beforeunload", function (e) {
    if (saveTimer || saving) { e.preventDefault(); e.returnValue = ""; }
  });

  fetch(api + "?" + auth, { cache: "no-store" })
    .then(function (r) { return r.json(); })
    .then(function (state) { load(state); status(""); })
    .catch(function (e) { status("Could not load the overlay: " + e.message, true); });
})();
