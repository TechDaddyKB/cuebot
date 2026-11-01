// A starting point: shows the latest follower. Change anything you like, then click Save.
// Help: https://techdaddykb.github.io/cuebot/guides/widget-code/
function show(name) {
  document.getElementById("name").textContent = name;
}

// When the widget starts: the title, and the last follower Cuebot knows
window.addEventListener("onWidgetLoad", function (obj) {
  document.getElementById("title").textContent = obj.detail.fieldData.title;
  var latest = obj.detail.session.data["follower-latest"];
  if (latest && latest.name) show(latest.name);
});

// A new follow (this also makes the editor's Emulate > Follow button work)
window.addEventListener("onEventReceived", function (obj) {
  if (obj.detail.listener === "follower-latest") show(obj.detail.event.name);
});

// The stream totals changed (this still comes while alerts are paused)
window.addEventListener("onSessionUpdate", function (obj) {
  var latest = obj.detail.session["follower-latest"];
  if (latest && latest.name) show(latest.name);
});
