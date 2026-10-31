// Cuebot's Streamlabs-compatible widget runtime. It runs after se-widget.js in a Streamlabs widget's frame and turns
// Cuebot's StreamElements-style events into what Streamlabs gives custom code: onLoad and onEventReceived on document,
// the chat box's #chatlist_item rendering into #log, the event list's #eventlist_item rendering, the alert box's
// per-alert HTML and JS with its tokens, and goalLoad / goalEvent. Written for Cuebot from Streamlabs' public custom
// widget documentation and starter templates.
(function () {
  "use strict";

  // StreamElements listener → Streamlabs event type.
  var TYPES = {
    "follower-latest": "follow", "subscriber-latest": "subscription", "tip-latest": "donation", "cheer-latest": "bits",
    "raid-latest": "raid", "host-latest": "host", "superchat-latest": "superchat", "merch-latest": "merch",
    "charityCampaignDonation-latest": "twitchcharitydonation", "sponsor-latest": "subscription",
    "redemption-latest": "redemption", "kicks-latest": "kicks", "jewels-latest": "jewels"
  };

  // Streamlabs goal type → Cuebot session key.
  var GOALS = { tip: "tip", follower: "follower", sub: "subscriber", bit: "cheer", superchat: "superchat" };

  var fieldData = {};
  var currency = { code: "USD", symbol: "$" };
  var session = {};
  var mode = "generic";

  function fire(name, detail) {
    document.dispatchEvent(new CustomEvent(name, { detail: detail }));
  }

  function escapeHtml(text) {
    return String(text == null ? "" : text).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  /** Fills {token}s whose names are in values (already HTML); any other braces stay as they are. */
  function fill(template, values) {
    return String(template).replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g, function (m, key) {
      return Object.prototype.hasOwnProperty.call(values, key) ? values[key] : m;
    });
  }

  function money(amount, code) {
    var n = Number(amount) || 0;
    try {
      return new Intl.NumberFormat(undefined, { style: "currency", currency: code || currency.code }).format(n);
    } catch (e) {
      return currency.symbol + n.toFixed(2);
    }
  }

  function isMoney(type) {
    return type === "donation" || type === "superchat" || type === "merch" || type === "twitchcharitydonation";
  }

  /** A StreamElements alert event as Streamlabs' generic event detail. */
  function slEvent(listener, e) {
    var type = TYPES[listener] || String(listener).replace(/-latest$/, "");
    var name = e.displayName || e.name || "";
    var amount = Number(e.amount) || 0;
    var formatted = isMoney(type) ? money(amount, e.currency) : String(amount || "");
    var now = new Date();
    var detail = {
      type: type,
      from: name,
      name: name,
      to: "",
      message: e.message || "",
      amount: amount,
      formattedAmount: formatted,
      currency: e.currency || currency.code,
      tag: tag(type, e, formatted),
      isTest: !!e.isTest,
      createdAt: now.toISOString().replace("T", " ").slice(0, 19),
      createdAtTimestamp: now.getTime()
    };
    if (type === "subscription") {
      detail.months = Number(e.amount) || 1;
      detail.gifted = !!e.gifted;
      detail.sender = e.sender || "";
    }
    if (type === "merch") detail.product = e.items || "";
    detail.payload = Object.assign({}, detail);
    return detail;
  }

  function tag(type, e, formatted) {
    switch (type) {
      case "follow": return "Follow";
      case "subscription": return e.bulkGifted ? "Gifted " + (Number(e.amount) || 1) : "Sub" + (Number(e.amount) > 1 ? " x" + e.amount : "");
      case "bits": return (Number(e.amount) || 0) + " Bits";
      case "raid": case "host": return (Number(e.amount) || 0) + " Raiders";
      case "merch": return e.items || "Merch";
      default: return formatted || type;
    }
  }

  function templateOf(id) {
    var t = document.getElementById(id);
    return t ? t.innerHTML : null;
  }

  function element(html) {
    var holder = document.createElement("div");
    holder.innerHTML = html.trim();
    return holder.firstElementChild;
  }

  // ---- Chat box ----------------------------------------------------------------------------------------------

  function chatMessage(event) {
    var d = event.data || {};
    var tags = d.tags || {};
    var log = document.getElementById("log");
    var template = templateOf("chatlist_item");
    if (log && template) {
      var item = element(fill(template, {
        from: escapeHtml(d.displayName || d.nick),
        messageId: escapeHtml(d.msgId),
        color: escapeHtml(d.displayColor),
        message: event.renderedText != null ? event.renderedText : escapeHtml(d.text)
      }));
      if (item) {
        item.setAttribute("data-user-id", d.userId || "");
        var badges = item.querySelector(".badges");
        (d.badges || []).forEach(function (b) {
          if (!badges || !b.url) return;
          var img = document.createElement("img");
          img.className = "badge";
          img.src = b.url;
          img.alt = b.type || "";
          badges.appendChild(img);
        });
        log.appendChild(item);
        while (log.children.length > 100) log.removeChild(log.firstElementChild);
      }
    }
    var service = event.service || "twitch";
    fire("onEventReceived", {
      command: "PRIVMSG",
      from: d.displayName || d.nick || "",
      body: d.text || "",
      platform: service + "_account",
      messageId: d.msgId || "",
      owner: tags.broadcaster === "1",
      subscriber: tags.subscriber === "1",
      userType: tags.mod === "1" ? "mod" : tags.vip === "1" ? "vip" : "user",
      tags: tags,
      payload: { raw: "" }
    });
  }

  function removeWhere(selector) {
    var log = document.getElementById("log");
    if (!log) return;
    Array.prototype.slice.call(log.querySelectorAll(selector)).forEach(function (n) { n.remove(); });
  }

  // ---- Event list --------------------------------------------------------------------------------------------

  function eventList() {
    var list = document.querySelector(".widget-EventList") || document.getElementById("sl-eventlist");
    if (!list) {
      list = document.createElement("ul");
      list.id = "sl-eventlist";
      list.className = "widget-EventList";
      document.body.appendChild(list);
    }
    return list;
  }

  function listEvent(detail) {
    var template = templateOf("eventlist_item");
    if (!template) return;
    var item = element(fill(template, {
      eventType: escapeHtml(detail.type), tag: escapeHtml(detail.tag), from: escapeHtml(detail.from),
      message: escapeHtml(detail.message), amount: escapeHtml(detail.formattedAmount)
    }));
    if (!item) return;
    var list = eventList();
    list.insertBefore(item, list.firstChild);
    var max = Number(fieldData.max_events) || 10;
    while (list.children.length > max) list.removeChild(list.lastElementChild);
  }

  // ---- Alert box ---------------------------------------------------------------------------------------------

  var MESSAGE_TEMPLATES = {
    follow: "{name} just followed!",
    subscription: "{name} just subscribed!",
    donation: "{name} donated {amount}!",
    bits: "{name} cheered {amount} bits!",
    raid: "{name} is raiding with {amount} viewers!",
    host: "{name} is hosting with {amount} viewers!",
    superchat: "{name} sent a Super Chat of {amount}!",
    merch: "{name} bought {product}!",
    twitchcharitydonation: "{name} donated {amount} to charity!"
  };

  var alertTimer = null;

  function alertRoot() {
    var root = document.getElementById("sl-alert");
    if (!root) {
      root = document.createElement("div");
      root.id = "sl-alert";
      document.body.appendChild(root);
    }
    return root;
  }

  function showAlert(detail, e) {
    var amount = isMoney(detail.type) ? detail.formattedAmount : String(detail.amount || "");
    var tokens = {
      name: '<span class="sl-name">' + escapeHtml(detail.name) + "</span>",
      amount: '<span class="sl-amount">' + escapeHtml(amount) + "</span>",
      count: escapeHtml(detail.amount),
      message: escapeHtml(detail.message),
      userMessage: escapeHtml(detail.message),
      product: escapeHtml(detail.product || ""),
      img: escapeHtml(e.avatar || fieldData.alertImage || "")
    };
    var templateKey = "messageTemplate_" + detail.type;
    var rawTemplate = typeof fieldData[templateKey] === "string" && fieldData[templateKey]
      ? fieldData[templateKey] : MESSAGE_TEMPLATES[detail.type] || "{name}";
    var messageTemplate = escapeHtml(rawTemplate);
    tokens.messageTemplate = fill(messageTemplate, tokens);

    var root = alertRoot();
    clearTimeout(alertTimer);
    root.innerHTML = fill(window.__slAlertHtml || "", tokens);
    root.setAttribute("data-type", detail.type);
    if (window.__slAlertJs) {
      var script = document.createElement("script");
      // In the JS a token is the plain value, safe inside a string literal.
      var plain = { name: detail.name, amount: amount, count: detail.amount, message: detail.message, userMessage: detail.message,
        product: detail.product || "", img: e.avatar || fieldData.alertImage || "" };
      var jsTokens = {};
      Object.keys(plain).forEach(function (k) {
        jsTokens[k] = JSON.stringify(String(plain[k] == null ? "" : plain[k])).slice(1, -1).replace(/</g, "\\u003c");
      });
      jsTokens.messageTemplate = JSON.stringify(fill(rawTemplate, {
        name: detail.name, amount: amount, product: detail.product || "" })).slice(1, -1).replace(/</g, "\\u003c");
      script.textContent = fill(window.__slAlertJs, jsTokens);
      root.appendChild(script);
    }
    fire("onEventReceived", detail);
    var seconds = Number(fieldData.widgetDuration) || 8;
    alertTimer = setTimeout(function () { root.innerHTML = ""; }, seconds * 1000);
  }

  // ---- Goal --------------------------------------------------------------------------------------------------

  function goalDetail() {
    var type = fieldData.goalType || "tip";
    var data = session[(GOALS[type] || type) + "-goal"] || {};
    var current = Number(data.amount) || 0;
    var target = Number(fieldData.goalTarget) || 100;
    var start = Number(fieldData.goalStart) || 0;
    var format = type === "tip" || type === "superchat" ? function (n) { return money(n); } : function (n) { return String(n); };
    return {
      type: type,
      title: fieldData.goalTitle || "Goal",
      percent: Math.round(Math.max(0, Math.min(100, (current - start) / Math.max(1, target - start) * 100))),
      ends_at: fieldData.goalEnds || "",
      to_go: { value: 0, unit: "", formatted_value: "" },
      amount: {
        current: current, target: target, start: start, manual_start: start,
        currency: currency.code, currency_symbol: currency.symbol,
        formatted_current: format(current), formatted_target: format(target), formatted_start: format(start)
      },
      data: {}
    };
  }

  // ---- Wiring ------------------------------------------------------------------------------------------------

  window.addEventListener("onWidgetLoad", function (obj) {
    var d = obj.detail || {};
    fieldData = d.fieldData || {};
    mode = fieldData.widgetSlType || "generic";
    if (d.currency) currency = d.currency;
    session = (d.session && d.session.data) || {};
    if (mode === "goal") {
      fire("goalLoad", goalDetail());
      return;
    }
    fire("onLoad", Object.assign({}, fieldData));
    if (mode === "eventlist") {
      eventList();
      (d.recents || []).slice(0, Number(fieldData.max_events) || 10).reverse().forEach(function (r) {
        var listener = (r.type || "") + "-latest";
        if (TYPES[listener]) listEvent(slEvent(listener, r));
      });
    }
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    session = (obj.detail && obj.detail.session) || session;
    if (mode === "goal") fire("goalEvent", goalDetail());
  });

  window.addEventListener("onEventReceived", function (obj) {
    var listener = obj.detail && obj.detail.listener;
    var event = (obj.detail && obj.detail.event) || {};
    if (listener === "message") {
      if (mode === "chatbox") chatMessage(event);
      return;
    }
    if (listener === "delete-message") {
      if (event.msgId) removeWhere('[data-id="' + String(event.msgId).replace(/["\\]/g, "") + '"]');
      return;
    }
    if (listener === "delete-messages") {
      if (event.userId) removeWhere('[data-user-id="' + String(event.userId).replace(/["\\]/g, "") + '"]');
      return;
    }
    if (listener === "event:skip") {
      if (mode === "alertbox") { clearTimeout(alertTimer); alertRoot().innerHTML = ""; }
      return;
    }
    if (!TYPES[listener] || mode === "goal" || mode === "chatbox") return;
    var detail = slEvent(listener, event);
    if (mode === "alertbox") showAlert(detail, event);
    else if (mode === "eventlist") { listEvent(detail); fire("onEventReceived", detail); }
    else fire("onEventReceived", detail);
  });
})();
