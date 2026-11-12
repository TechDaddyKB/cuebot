// Cuebot goal bar: progress towards a goal of followers, subs, tips, bits, Kicks or revenue for this stream, week,
// month or all time, from the stream totals. Built on the StreamElements widget API and its session data.
(function () {
  "use strict";

  var settings = {};
  var data = {};
  var currency = "USD";
  var root = null;
  var lastProgress = null;

  var MONEY = { tip: true, superchat: true, charityCampaignDonation: true, merch: true, revenue: true };
  var AMOUNT = { tip: true, superchat: true, charityCampaignDonation: true, merch: true, revenue: true, cheer: true, kicks: true, jewels: true };
  var REVENUE = ["tip", "superchat", "charityCampaignDonation", "merch"];

  function num(value) { return Number(value) || 0; }

  // The Ko-fi goal Cuebot keeps (Webhooks page): its own title, target, progress and currency.
  function kofi() { return settings.type === "kofi" ? (data["kofi-goal"] || { current: 0, target: 0 }) : null; }

  // Patreon totals Cuebot keeps (Webhooks page): paying patrons or the monthly pledges; the target is the Goal setting.
  function patreon() {
    return settings.type === "patreonPatrons" || settings.type === "patreonMonthly"
      ? (data["patreon"] || { paidCount: 0, monthly: 0, currency: "" }) : null;
  }

  // Fourthwall members Cuebot knows (Webhooks page; from webhooks and the API's member list).
  function fourthwall() { return settings.type === "fourthwallMembers" ? (data["fourthwall"] || { memberCount: 0 }) : null; }

  function useAmount() {
    if (kofi()) return true;
    if (patreon()) return settings.type === "patreonMonthly";
    if (fourthwall()) return false;
    if (settings.measure === "amount") return true;
    if (settings.measure === "count") return false;
    return !!AMOUNT[settings.type];
  }

  function progress() {
    var k = kofi();
    if (k) return num(k.current) + num(settings.start);
    var p = patreon();
    if (p) return num(settings.type === "patreonMonthly" ? p.monthly : p.paidCount) + num(settings.start);
    var f = fourthwall();
    if (f) return num(f.memberCount) + num(settings.start);
    var types = settings.type === "revenue" ? REVENUE : [settings.type || "follower"];
    var field = useAmount() ? "amount" : "count";
    var period = settings.period || "session";
    return types.reduce(function (sum, t) {
      var item = data[t + "-" + period];
      return sum + (item ? num(item[field]) : 0);
    }, num(settings.start));
  }

  function format(value) {
    if (useAmount() && (MONEY[settings.type] || kofi() || patreon())) {
      var code = (kofi() && kofi().currency) || (patreon() && patreon().currency) || currency;
      try { return new Intl.NumberFormat(undefined, { style: "currency", currency: code }).format(value); }
      catch (err) { return String(value); }
    }
    return String(Math.round(value * 100) / 100);
  }

  function render() {
    if (!root) return;
    var k = kofi();
    var goal = Math.max(1, (k && num(k.target)) || num(settings.goal) || 100);
    var value = progress();
    var percent = Math.min(100, Math.floor(value / goal * 100));
    var done = value >= goal;
    root.querySelector(".title").textContent = settings.title || (k && k.title) || "";
    root.querySelector(".fill").style.width = Math.min(100, value / goal * 100) + "%";
    root.classList.toggle("done", done);
    var text;
    if (done && settings.doneText) text = settings.doneText;
    else if (settings.numbers === "progress") text = format(value) + " / " + format(goal);
    else if (settings.numbers === "percent") text = percent + "%";
    else if (settings.numbers === "none") text = "";
    else text = format(value) + " / " + format(goal) + " (" + percent + "%)";
    root.querySelector(".numbers").textContent = text;
    if (lastProgress !== null && value > lastProgress) {
      root.classList.remove("bump");
      void root.offsetWidth;   // restart the animation
      root.classList.add("bump");
    }
    lastProgress = value;
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    data = (obj.detail.session && obj.detail.session.data) || {};
    currency = (obj.detail.currency && obj.detail.currency.code) || "USD";
    root = document.getElementById("goal");
    lastProgress = null;
    render();
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    data = obj.detail.session || {};
    render();
  });
})();
