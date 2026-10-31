// Cuebot charity: a Twitch charity campaign's progress (charity, logo, raised of the target) while one runs, otherwise
// the charity donations Cuebot counted (Streamlabs, StreamElements, DonorDrive, Twitch) against your own goal, with the
// latest donation. The last campaign is kept in the widget store, so a reload shows it at once. Built on the
// StreamElements widget API; campaigns arrive as Cuebot's "cuebot-charity" event.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var data = {};
  var campaign = null;
  var latest = null;
  var currency = "USD";
  var editor = false;
  var KEY = "cuebot-charity:campaign";

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }

  function money(amount, code) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: code || currency }).format(amount); }
    catch (err) { return String(amount); }
  }

  function render() {
    if (!root) return;
    var live = campaign && campaign.status === "active" && num(campaign.target, 0) > 0;
    var raised = live ? num(campaign.raised, 0) : num((data["charityCampaignDonation-" + (settings.period || "session")] || {}).amount, 0);
    var goal = live ? num(campaign.target, 1) : Math.max(1, num(settings.goal, 500));
    var code = live ? campaign.currency : currency;
    root.querySelector(".title").textContent = settings.title || (live ? campaign.charity : "") || "Charity";
    var logo = root.querySelector(".logo");
    logo.setAttribute("src", bool(settings.showLogo) && live && /^https?:\/\//.test(campaign.logo || "") ? campaign.logo : "");
    root.querySelector(".fill").style.width = Math.min(100, raised / goal * 100) + "%";
    root.querySelector(".numbers").textContent = raised >= goal && settings.doneText
      ? settings.doneText
      : money(raised, code) + " / " + money(goal, code) + " (" + Math.floor(raised / goal * 100) + "%)";
    root.querySelector(".latest").textContent = bool(settings.showLatest) && latest && latest.name
      ? "Latest: " + latest.name + " " + money(num(latest.amount, 0), latest.currency || code)
      : "";
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("charity");
    editor = !!(obj.detail.overlay && obj.detail.overlay.isEditorMode);
    data = (obj.detail.session && obj.detail.session.data) || {};
    currency = (obj.detail.currency && obj.detail.currency.code) || "USD";
    var last = data["charityCampaignDonation-latest"];
    latest = last && last.name ? last : null;
    render();
    if (window.SE_API) SE_API.store.get(KEY).then(function (saved) { if (saved && !campaign) { campaign = saved; render(); } }).catch(function () { });
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    data = obj.detail.session || {};
    render();
  });

  window.addEventListener("onEventReceived", function (obj) {
    var listener = obj.detail.listener;
    var e = obj.detail.event || {};
    if (listener === "cuebot-charity") {
      campaign = e;
      if (window.SE_API && !editor) SE_API.store.set(KEY, e);
      render();
    } else if (listener === "charityCampaignDonation-latest") {
      latest = e;
      render();
    }
  });
})();
