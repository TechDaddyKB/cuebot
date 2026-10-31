// Cuebot label: one value from the stream totals (latest name, a count or amount for the stream, week, month or all
// time, the top donation or donator, recent names), updated as alerts come in. Built on the StreamElements widget API
// and its session data, so it is edited like any other widget.
(function () {
  "use strict";

  var settings = {};
  var data = {};
  var currency = "USD";
  var root = null;
  var last = null;

  var MONEY = { tip: true, superchat: true, charityCampaignDonation: true, merch: true, revenue: true };
  var REVENUE = ["tip", "superchat", "charityCampaignDonation", "merch"];
  var TOP_PERIOD = { session: "session", week: "weekly", month: "monthly", total: "alltime" };

  function bool(value) { return value === true || value === "true"; }
  function num(value) { return Number(value) || 0; }

  function money(amount, code) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: code || currency }).format(amount); }
    catch (err) { return String(amount); }
  }

  function amountText(type, amount, code) {
    if (MONEY[type]) return money(amount, code);
    return Math.round(amount * 100) / 100 + "";
  }

  /** The types a setting stands for: revenue is tips, Super Chats and charity donations together. */
  function types(type) { return type === "revenue" ? REVENUE : [type]; }

  function newest(type, key) {
    var best = null;
    types(type).forEach(function (t) {
      var item = data[t + "-" + key];
      if (!item || !item.name) return;
      if (!best || String(item.createdAt || "") > String(best.createdAt || "")) best = item;
    });
    return best;
  }

  function largest(type, key) {
    var best = null;
    types(type).forEach(function (t) {
      var item = data[t + "-" + key];
      if (item && item.name && (!best || num(item.amount) > num(best.amount))) best = item;
    });
    return best;
  }

  function tally(type, field) {
    return types(type).reduce(function (sum, t) {
      var item = data[t + "-" + (settings.period || "session")];
      return sum + (item ? num(item[field]) : 0);
    }, 0);
  }

  function recent(type) {
    var items = [];
    types(type).forEach(function (t) { items = items.concat(data[t + "-recent"] || []); });
    items.sort(function (a, b) { return String(b.createdAt || "") < String(a.createdAt || "") ? -1 : 1; });
    var max = Math.max(1, num(settings.recentCount) || 5);
    return items.filter(function (i) { return i && i.name; }).slice(0, max).map(function (i) { return i.name; });
  }

  /** The text for the value, or null when there is nothing yet. */
  function value() {
    var type = settings.type || "follower";
    var top = TOP_PERIOD[settings.period] || "session";
    switch (settings.show) {
      case "latestAmount": {
        var l = newest(type, "latest");
        return l ? l.name + " " + amountText(type, num(l.amount), l.currency) : null;
      }
      case "count": return String(tally(type, "count"));
      case "amount": return amountText(type, tally(type, "amount"));
      case "topDonation": {
        var d = largest(type, top + "-top-donation");
        return d ? d.name + " " + amountText(type, num(d.amount)) : null;
      }
      case "topDonator": {
        var p = largest(type, top + "-top-donator");
        return p ? p.name + " " + amountText(type, num(p.amount)) : null;
      }
      case "recent": {
        var names = recent(type);
        return names.length ? names.join(settings.separator === undefined ? " • " : settings.separator) : null;
      }
      default: {
        var latest = newest(type, "latest");
        return latest ? latest.name : null;
      }
    }
  }

  function render() {
    if (!root) return;
    var text = value();
    var shown = text === null ? String(settings.emptyText || "") : text;
    root.querySelector(".prefix").textContent = settings.prefix || "";
    root.querySelector(".suffix").textContent = settings.suffix || "";
    var el = root.querySelector(".value");
    el.textContent = shown;
    if (last !== null && last !== shown && bool(settings.flash)) {
      root.classList.remove("flash");
      void root.offsetWidth;   // restart the animation
      root.classList.add("flash");
    }
    last = shown;
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    data = (obj.detail.session && obj.detail.session.data) || {};
    currency = (obj.detail.currency && obj.detail.currency.code) || "USD";
    root = document.getElementById("label");
    root.classList.toggle("shadow", bool(settings.shadow));
    last = null;
    render();
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    data = obj.detail.session || {};
    render();
  });
})();
