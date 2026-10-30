// Cuebot event list: the latest follows, subs, tips, cheers, raids, Super Chats, Kicks and more from every platform,
// newest first. Starts from the recent alerts Cuebot keeps and adds new ones as they happen. Built on the
// StreamElements widget API, so it is edited like any other widget.
(function () {
  "use strict";

  var settings = {};
  var currency = "USD";
  var root = null;

  var SHOW = {
    follower: "showFollower", subscriber: "showSubscriber", tip: "showTip", cheer: "showCheer", raid: "showRaid",
    superchat: "showSuperchat", kicks: "showKicks", jewels: "showJewels", charityCampaignDonation: "showCharity",
    redemption: "showRedemption"
  };
  var PLATFORMS = { twitch: "Twitch", kick: "Kick", rumble: "Rumble", youtube: "YouTube", streamelements: "StreamElements" };

  function bool(value) { return value === true || value === "true"; }
  function num(value) { return Number(value) || 0; }

  function money(amount, code) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: code || currency }).format(amount); }
    catch (err) { return String(amount); }
  }

  /** Recents carry StreamElements' event type; alerts arrive on "{type}-latest". */
  function kind(type) { return type === "channelPointsRedemption" ? "redemption" : type; }

  function describe(type, e) {
    var amount = num(e.amount);
    switch (type) {
      case "follower": return "followed";
      case "subscriber":
        if (bool(e.bulkGifted)) return "gifted " + amount + " subs";
        if (bool(e.gifted)) return "got a gift sub" + (e.sender ? " from " + e.sender : "");
        return amount > 1 ? "subscribed for " + amount + " months" : "subscribed";
      case "tip": return "tipped " + money(amount, e.currency);
      case "charityCampaignDonation": return "donated " + money(amount, e.currency) + " to charity";
      case "superchat": return "sent a " + money(amount, e.currency) + " Super Chat";
      case "cheer": return "cheered " + amount + " bits";
      case "raid": return "raided with " + amount;
      case "kicks": return "sent " + amount + " Kicks";
      case "jewels": return "sent " + amount + " Jewels";
      case "redemption": return "redeemed " + (e.redemption || "a reward");
      default: return "";
    }
  }

  function build(type, e) {
    var item = document.createElement("div");
    item.className = "event " + (e.service || "");
    var line = document.createElement("div");
    var name = document.createElement("span");
    name.className = "name";
    name.textContent = e.name || e.displayName || "";
    var what = document.createElement("span");
    what.className = "what";
    what.textContent = describe(type, e);
    line.appendChild(name);
    line.appendChild(document.createTextNode(" "));
    line.appendChild(what);
    if (bool(settings.showMessage) && e.message && (type === "tip" || type === "superchat" || type === "charityCampaignDonation")) {
      var message = document.createElement("span");
      message.className = "message";
      message.textContent = e.message;
      line.appendChild(message);
    }
    item.appendChild(line);
    if (bool(settings.showPlatform) && PLATFORMS[e.service]) {
      var platform = document.createElement("span");
      platform.className = "platform";
      platform.textContent = PLATFORMS[e.service];
      item.appendChild(platform);
    }
    return item;
  }

  function add(type, e, animate) {
    if (!SHOW[type] || !bool(settings[SHOW[type]]) || !(e.name || e.displayName)) return;
    var item = build(type, e);
    if (!animate) item.style.animation = "none";
    if (settings.direction === "bottom") root.appendChild(item);
    else root.insertBefore(item, root.firstChild);
    var max = Math.max(1, num(settings.maxItems) || 6);
    while (root.children.length > max) {
      var oldest = settings.direction === "bottom" ? root.firstChild : root.lastChild;
      root.removeChild(oldest);
    }
    var hide = num(settings.hideAfter);
    if (hide > 0 && animate) {
      setTimeout(function () {
        item.classList.add("leaving");
        setTimeout(function () { if (item.parentNode) item.parentNode.removeChild(item); }, 400);
      }, hide * 1000);
    }
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    currency = (obj.detail.currency && obj.detail.currency.code) || "USD";
    root = document.getElementById("events");
    root.textContent = "";
    if (num(settings.hideAfter) > 0) return;   // events that hide again are only shown as they happen
    // Recents are newest first; add the oldest first so the newest ends up first.
    var recents = (obj.detail.recents || []).slice();
    if (!recents.length && obj.detail.overlay && obj.detail.overlay.isEditorMode) {
      recents = [
        { type: "tip", name: "Generous", amount: 5, currency: currency, service: "rumble", message: "Great stream!" },
        { type: "subscriber", name: "LoyalSub", amount: 12, service: "twitch" },
        { type: "follower", name: "NewFriend", service: "kick" }
      ];
    }
    recents.reverse().forEach(function (r) { add(kind(r.type), r, false); });
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (!root) return;
    var listener = obj.detail.listener || "";
    if (/-latest$/.test(listener)) add(listener.replace(/-latest$/, ""), obj.detail.event || {}, true);
  });
})();
