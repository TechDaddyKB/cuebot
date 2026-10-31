// Cuebot hype boss: follows, subs, bits, Kicks, tips, Super Chats and raids from every platform damage the boss; whoever
// lands the final hit becomes the next boss, with more health. The boss's own support heals them. The fight is kept in
// the widget store under its name, so reloading OBS keeps it. Built on the StreamElements widget API.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var boss = null;   // {name, avatar, hp, maxHp, level}

  function bool(value) { return value === true || value === "true"; }
  function num(value, fallback) { var n = Number(value); return isFinite(n) && value !== "" && value !== undefined ? n : fallback; }
  function key() { return "cuebot-hypeboss:" + (String(settings.fightName || "hype-boss").trim() || "hype-boss"); }

  function firstBoss() {
    var hp = Math.max(1, num(settings.startHp, 1000));
    return { name: settings.bossName || "The streamer", avatar: "", hp: hp, maxHp: hp, level: 1 };
  }

  function save() { if (window.SE_API) SE_API.store.set(key(), boss); }

  function render() {
    if (!root || !boss) return;
    root.querySelector(".name").textContent = boss.name;
    root.querySelector(".level").textContent = "Boss level " + boss.level;
    root.querySelector(".hp").style.width = Math.max(0, boss.hp / boss.maxHp * 100) + "%";
    root.querySelector(".numbers").textContent = Math.ceil(boss.hp) + " / " + boss.maxHp;
    var img = root.querySelector(".avatar img");
    img.setAttribute("src", /^https?:\/\//.test(boss.avatar || "") ? boss.avatar : "");
    root.querySelector(".initial").textContent = (boss.name || "?").charAt(0).toUpperCase();
  }

  function popup(text, kind) {
    var el = document.createElement("div");
    el.className = "popup " + kind;
    el.textContent = text;
    var area = root.querySelector(".popups");
    el.style.left = 30 + Math.random() * 50 + "%";
    el.style.top = 20 + Math.random() * 40 + "%";
    area.appendChild(el);
    setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 1500);
  }

  function replay(cls) {
    root.classList.remove(cls);
    void root.offsetWidth;   // restart the animation
    root.classList.add(cls);
  }

  /** Applies one supporter's damage; the boss's own support heals, a final hit makes the attacker the next boss. */
  function hit(name, avatar, damage) {
    if (!boss || !name || damage <= 0) return;
    damage = Math.round(damage);
    if (name.toLowerCase() === String(boss.name).toLowerCase()) {
      if (!bool(settings.selfHeal)) return;
      boss.hp = Math.min(boss.maxHp, boss.hp + damage);
      if (avatar) boss.avatar = avatar;
      popup("+" + damage, "heal");
    } else {
      boss.hp -= damage;
      popup("-" + damage, "damage");
      replay("hit");
      if (boss.hp <= 0) {
        var maxHp = boss.maxHp + Math.max(0, num(settings.hpIncrease, 500));
        boss = { name: name, avatar: avatar || "", hp: maxHp, maxHp: maxHp, level: boss.level + 1 };
        replay("defeated");
      }
    }
    save();
    render();
  }

  function damageFor(type, e) {
    var amount = num(e.amount, 1);
    switch (type) {
      case "follower": return num(settings.perFollow, 10);
      case "subscriber": return num(settings.perSub, 100) * (bool(e.bulkGifted) ? Math.max(1, amount) : 1);
      case "cheer": case "kicks": return amount * num(settings.perBit, 1);
      case "tip": case "superchat": case "charityCampaignDonation": case "merch": return amount * num(settings.perDollar, 100);
      case "raid": return amount * num(settings.perRaider, 2);
      default: return 0;
    }
  }

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("boss");
    boss = firstBoss();
    render();
    if (!window.SE_API) return;
    SE_API.store.get(key()).then(function (saved) {
      if (saved && saved.name && num(saved.maxHp, 0) > 0) {
        boss = { name: saved.name, avatar: saved.avatar || "", hp: num(saved.hp, saved.maxHp), maxHp: num(saved.maxHp, 1), level: num(saved.level, 1) };
        render();
      }
    }).catch(function () { });
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (!root) return;
    var listener = obj.detail.listener || "";
    var e = obj.detail.event || {};
    if (/-latest$/.test(listener)) {
      // Gift subs: the gifter attacks, not the recipient.
      var name = bool(e.gifted) && e.sender ? e.sender : e.name || e.displayName;
      hit(name, bool(e.gifted) && e.sender ? "" : e.avatar, damageFor(listener.replace(/-latest$/, ""), e));
    } else if (listener === "event:test" && e.listener === "widget-button") {
      if (e.field === "reset") { boss = firstBoss(); save(); render(); }
      if (e.field === "testHit") hit("Test hero", "", Math.max(1, Math.ceil(boss.maxHp / 4)));
    }
  });
})();
