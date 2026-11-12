// Cuebot product showcase: rotates through the products you enter (image, name, price) with where to buy them, and
// shows who just bought merch when Cuebot hears of a purchase (StreamElements, Streamlabs, Fourthwall, Shopify, Ko-fi).
// Works for any shop. "My Fourthwall shop" / "My Shopify store" show the products Cuebot read from Fourthwall or Shopify
// (session data "fourthwall" / "shopify": names, prices, pictures, sold out) and follow changes. Built on the StreamElements widget API, so it is edited like any
// other widget.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var products = [];
  var at = 0;
  var rotateTimer = null;
  var purchaseTimer = null;

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }

  function show(i) {
    if (!products.length) { root.querySelector(".card").style.display = "none"; return; }
    root.querySelector(".card").style.display = "";
    var p = products[i % products.length];
    root.querySelector(".picture img").setAttribute("src", p.image);
    root.querySelector(".name").textContent = p.name;
    root.querySelector(".price").textContent = p.price;
    root.querySelector(".shop").textContent = settings.shopText || "";
    var card = root.querySelector(".card");
    card.classList.remove("enter");
    void card.offsetWidth;   // restart the animation
    card.classList.add("enter");
  }

  function money(amount, code) {
    try { return new Intl.NumberFormat(undefined, { style: "currency", currency: code || "USD" }).format(amount); }
    catch (err) { return String(amount); }
  }

  function fromShop(data) {
    var source = settings.source === "shopify" ? "shopify" : "fourthwall";
    var shop = (data && data[source] && data[source].products) || [];
    return shop.map(function (p) {
      return { name: p.name || "", price: p.soldOut ? "Sold out" : (p.price || ""), image: p.image || "" };
    }).filter(function (p) { return p.name; });
  }

  function start(list) {
    var same = JSON.stringify(list) === JSON.stringify(products);
    products = list;
    if (same && rotateTimer) return;
    at = 0;
    show(0);
    clearInterval(rotateTimer);
    rotateTimer = null;
    if (products.length > 1) rotateTimer = setInterval(function () { show(++at); }, Math.max(2, num(settings.rotateSeconds, 8)) * 1000);
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("products");
    var list = [];
    if (settings.source === "fourthwall" || settings.source === "shopify") list = fromShop((obj.detail.session && obj.detail.session.data) || {});
    else {
      for (var i = 1; i <= 6; i++) {
        var name = String(settings["product" + i + "Name"] || "").trim();
        if (name) list.push({ name: name, price: settings["product" + i + "Price"] || "", image: settings["product" + i + "Image"] || "" });
      }
    }
    products = [];
    start(list);
  });

  window.addEventListener("onSessionUpdate", function (obj) {
    if ((settings.source !== "fourthwall" && settings.source !== "shopify") || !root) return;
    start(fromShop(obj.detail.session || {}));
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (obj.detail.listener !== "merch-latest" || !bool(settings.showPurchases)) return;
    var e = obj.detail.event || {};
    var banner = root.querySelector(".purchase");
    banner.textContent = (e.name || "Someone") + " just bought " + (e.items || "merch") +
      (Number(e.amount) > 0 ? " (" + (e.formattedAmount || money(Number(e.amount), e.currency)) + ")" : "") + "!";
    banner.classList.add("show");
    clearTimeout(purchaseTimer);
    purchaseTimer = setTimeout(function () { banner.classList.remove("show"); }, Math.max(2, num(settings.purchaseSeconds, 8)) * 1000);
  });
})();
