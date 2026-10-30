// Cuebot chat box: chat from every connected platform in one list, built on the StreamElements widget API, so it can
// be edited like any other widget. The "youtube" layout builds YouTube popout chat's element structure, so YouTube
// chat CSS themes pasted into My CSS restyle it.
(function () {
  "use strict";

  var settings = {};
  var chat = null;
  var ignored = [];
  var PLATFORM_NAMES = { twitch: "Twitch", kick: "Kick", rumble: "Rumble", youtube: "YouTube" };

  function bool(value) { return value === true || value === "true"; }

  function el(tag, attrs, text) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) { node.setAttribute(k, attrs[k]); });
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function add(node, key) {
    node.dataset.key = key || "";
    node.classList.add("anim-" + (settings.animation || "slide"));
    if ((settings.direction || "bottom") === "top") chat.insertBefore(node, chat.firstChild);
    else chat.appendChild(node);
    var max = Math.max(1, Number(settings.maxMessages) || 20);
    var items = chat.children;
    while (items.length > max) {
      var oldest = settings.direction === "top" ? items[items.length - 1] : items[0];
      oldest.remove();
    }
    var hide = Number(settings.hideAfter) || 0;
    if (hide > 0) {
      setTimeout(function () {
        node.classList.add("leaving");
        setTimeout(function () { node.remove(); }, 400);
      }, hide * 1000);
    }
  }

  function remove(selector) {
    chat.querySelectorAll(selector).forEach(function (node) { node.remove(); });
  }

  // ---- Cuebot layout ----------------------------------------------------------------------------------------------

  function cuebotMessage(data, service, html) {
    var msg = el("div", { class: "msg " + service, "data-msgid": data.msgId || "", "data-user": data.userId || "" });
    var head = el("span", { class: "head" });
    if (bool(settings.showPlatform) && PLATFORM_NAMES[service]) head.appendChild(el("span", { class: "platform " + service }, PLATFORM_NAMES[service]));
    if (bool(settings.showAvatars) && data.avatar) head.appendChild(el("img", { class: "avatar", src: data.avatar, alt: "" }));
    if (bool(settings.showBadges) && data.badges && data.badges.length) {
      var badges = el("span", { class: "badges" });
      data.badges.forEach(function (b) { badges.appendChild(el("img", { src: b.url, alt: b.description || b.type, title: b.description || b.type })); });
      head.appendChild(badges);
    }
    var name = el("span", { class: "name" }, data.displayName || data.nick || "");
    if (bool(settings.useNameColors) && data.displayColor) name.style.color = data.displayColor;
    head.appendChild(name);
    if (bool(settings.showPronouns) && data.pronouns) head.appendChild(el("span", { class: "pronouns" }, data.pronouns));
    msg.appendChild(head);
    var text = el("span", { class: "text" });
    text.innerHTML = html;   // Cuebot's renderedText: escaped text with emote images
    msg.appendChild(text);
    return msg;
  }

  // ---- YouTube popout layout --------------------------------------------------------------------------------------

  function authorType(data) {
    var tags = data.tags || {};
    if (tags.broadcaster === "1") return "owner";
    if (tags.mod === "1") return "moderator";
    if (tags.subscriber === "1") return "member";
    return "";
  }

  function youtubeMessage(data, service, html) {
    var type = authorType(data);
    var item = el("yt-live-chat-text-message-renderer", {
      class: "style-scope yt-live-chat-item-list-renderer", "author-type": type, "data-msgid": data.msgId || "",
      "data-user": data.userId || "", "data-service": service
    });
    var photo = el("yt-img-shadow", { id: "author-photo", class: "no-transition style-scope yt-live-chat-text-message-renderer", height: "24", width: "24" });
    if (data.avatar) photo.appendChild(el("img", { id: "img", class: "style-scope yt-img-shadow", src: data.avatar, alt: "", height: "24", width: "24" }));
    item.appendChild(photo);

    var content = el("div", { id: "content", class: "style-scope yt-live-chat-text-message-renderer" });
    content.appendChild(el("span", { id: "timestamp", class: "style-scope yt-live-chat-text-message-renderer" },
      new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })));
    var chip = el("yt-live-chat-author-chip", { class: "style-scope yt-live-chat-text-message-renderer" });
    var name = el("span", { id: "author-name", dir: "auto", class: (type + " style-scope yt-live-chat-author-chip").trim() }, data.displayName || data.nick || "");
    name.appendChild(el("span", { id: "chip-badges", class: "style-scope yt-live-chat-author-chip" }));
    chip.appendChild(name);
    var badges = el("span", { id: "chat-badges", class: "style-scope yt-live-chat-author-chip" });
    if (bool(settings.showBadges)) {
      (data.badges || []).forEach(function (b) {
        var badge = el("yt-live-chat-author-badge-renderer", { class: "style-scope yt-live-chat-author-chip", type: b.type, "aria-label": b.description || b.type });
        var image = el("div", { id: "image", class: "style-scope yt-live-chat-author-badge-renderer" });
        image.appendChild(el("img", { src: b.url, class: "style-scope yt-live-chat-author-badge-renderer", alt: b.description || b.type }));
        badge.appendChild(image);
        badges.appendChild(badge);
      });
    }
    chip.appendChild(badges);
    content.appendChild(chip);
    var message = el("span", { id: "message", dir: "auto", class: "style-scope yt-live-chat-text-message-renderer" });
    message.innerHTML = html.replace(/class="emote"/g, 'class="emoji style-scope yt-live-chat-text-message-renderer"');
    content.appendChild(message);
    item.appendChild(content);
    return item;
  }

  // ---- Alerts -----------------------------------------------------------------------------------------------------

  var ALERTS = {
    "follower-latest": function (e) { return e.name + " followed"; },
    "subscriber-latest": function (e) {
      if (e.bulkGifted) return e.name + " gifted " + e.amount + " subs";
      if (e.gifted) return (e.sender || "Someone") + " gifted a sub to " + e.name;
      return e.amount > 1 ? e.name + " subscribed for " + e.amount + " months" : e.name + " subscribed";
    },
    "tip-latest": function (e) { return e.name + " tipped " + (e.formattedAmount || money(e.amount, e.currency)); },
    "superchat-latest": function (e) { return e.name + " sent a " + (e.formattedAmount || money(e.amount, e.currency)) + " Super Chat"; },
    "cheer-latest": function (e) { return e.name + " cheered " + e.amount + " bits"; },
    "kicks-latest": function (e) { return e.name + " sent " + e.amount + " Kicks"; },
    "raid-latest": function (e) { return e.name + " raided with " + e.amount + " viewers"; }
  };

  function money(amount, currency) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: currency || "USD" }).format(amount); }
    catch (err) { return String(amount); }
  }

  function alertLine(text) {
    if (settings.layout === "youtube") {
      var item = el("yt-live-chat-membership-item-renderer", { class: "style-scope yt-live-chat-item-list-renderer" });
      item.appendChild(el("div", { id: "header-subtext", class: "style-scope yt-live-chat-membership-item-renderer" }, text));
      return item;
    }
    return el("div", { class: "msg alert" }, text);
  }

  // ---- Events -----------------------------------------------------------------------------------------------------

  function onMessage(event) {
    var data = event.data || {};
    var service = event.service || "twitch";
    var text = String(data.text || "");
    if (!text.trim()) return;   // e.g. a Super Chat without a comment: its alert line says it all
    if (bool(settings.hideCommands) && text.charAt(0) === "!") return;
    var who = String(data.nick || data.displayName || "").toLowerCase();
    if (ignored.indexOf(who) !== -1) return;
    var html = event.renderedText || text.replace(/[&<>"]/g, function (c) { return "&#" + c.charCodeAt(0) + ";"; });
    add(settings.layout === "youtube" ? youtubeMessage(data, service, html) : cuebotMessage(data, service, html), data.msgId);
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    chat = document.getElementById("chat");
    ignored = String(settings.ignoredUsers || "").split(",").map(function (s) { return s.trim().toLowerCase(); }).filter(Boolean);
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (!chat) return;
    var listener = obj.detail.listener;
    var event = obj.detail.event || {};
    if (listener === "message") onMessage(event);
    else if (listener === "delete-message") remove('[data-msgid="' + CSS.escape(event.msgId || "") + '"]');
    else if (listener === "delete-messages") remove('[data-user="' + CSS.escape(event.userId || "") + '"]');
    else if (listener === "event:test" && event.listener === "widget-button" && event.field === "testMessage") {
      onMessage({
        service: "twitch",
        renderedText: "This is how chat looks with these settings.",
        data: {
          msgId: "test-" + Date.now(), userId: "test", nick: "cuebot", displayName: "Cuebot", displayColor: "#3d8bfd",
          text: "This is how chat looks with these settings.", badges: [], tags: { mod: "1" }, pronouns: "It/Its"
        }
      });
    } else if (bool(settings.showAlerts) && ALERTS[listener] && event.name) {
      add(alertLine(ALERTS[listener](event)));
      if (window.SE_API) SE_API.resumeQueue();
    }
  });
})();
