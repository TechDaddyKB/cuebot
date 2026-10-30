// Cuebot media request player: plays the media request queue (YouTube videos viewers asked for in chat on every
// platform) with YouTube's player, follows pause, skip and volume from Cuebot, moves on at the length limit, and shows
// what plays, who asked and what is next. Cuebot keeps the queue; the player reports when a video ends. In the editor
// preview it is muted and never moves the queue on. Built on the StreamElements widget API.
(function () {
  "use strict";

  var settings = {};
  var root = null;
  var state = null;      // Cuebot's media state
  var player = null;
  var ready = false;
  var loadedId = "";     // the request id the player has loaded
  var reported = "";     // the request id already reported as ended
  var editor = false;

  function bool(value) { return value === true || value === "true"; }
  function $(cls) { return root.querySelector("." + cls); }

  function report(id, failed) {
    if (editor || !id || reported === id || !window.SE_API) return;
    reported = id;
    var sent = SE_API.sendMessage("cuebot_media", { action: failed ? "failed" : "ended", id: id });
    if (sent && sent.catch) sent.catch(function () { });
  }

  function render() {
    if (!root) return;
    var current = state && state.current;
    root.classList.toggle("idle", !current);
    root.classList.toggle("no-video", !bool(settings.showVideo));
    var bar = $("bar");
    bar.classList.toggle("hidden", !bool(settings.showNowPlaying) && !(bool(settings.showNext)));
    if (current) {
      $("label").textContent = state.paused ? "Paused" : "Now playing";
      $("title").textContent = current.title;
      $("by").textContent = current.user ? "asked by " + current.user : "";
    } else {
      $("label").textContent = "";
      $("title").textContent = settings.idleText || "";
      $("by").textContent = bool(settings.showHow) && state && state.open ? "Type " + state.command + " and a YouTube link" : "";
    }
    $("now").style.display = bool(settings.showNowPlaying) ? "" : "none";
    var next = state && state.queue && state.queue[0];
    $("next").textContent = bool(settings.showNext) && next
      ? "Next: " + next.title + (state.queueLength > 1 ? "  (+" + (state.queueLength - 1) + " more)" : "")
      : "";
    sync();
  }

  /** Makes the player match Cuebot's state: the current video, paused or playing, the volume. */
  function sync() {
    if (!ready || !player) return;
    var current = state && state.current;
    if (!current) {
      if (loadedId) { player.stopVideo(); loadedId = ""; }
      return;
    }
    if (current.id !== loadedId) {
      loadedId = current.id;
      player.loadVideoById(current.videoId);
    }
    if (editor) player.mute();
    else { player.unMute(); player.setVolume(Math.max(0, Math.min(100, Number(state.volume) || 0))); }
    var playing = player.getPlayerState && player.getPlayerState() === 1;
    if (state.paused && playing) player.pauseVideo();
    if (!state.paused && !playing && player.getPlayerState() !== 3) player.playVideo();
  }

  function createPlayer() {
    player = new YT.Player("player", {
      width: "100%",
      height: "100%",
      playerVars: { autoplay: 1, controls: 0, disablekb: 1, modestbranding: 1, rel: 0, playsinline: 1, iv_load_policy: 3 },
      events: {
        onReady: function () { ready = true; sync(); },
        onStateChange: function (e) { if (e.data === 0 && state && state.current && onCurrent()) report(state.current.id); },
        // Not embeddable, removed, a bad id: Cuebot refunds it and the queue moves on rather than stall.
        onError: function () { if (state && state.current) report(state.current.id, true); }
      }
    });
  }

  /** Whether the player is playing the video Cuebot says is current (right after a switch it still reports the old one). */
  function onCurrent() {
    var data = player.getVideoData && player.getVideoData();
    return !!(data && state.current && data.video_id === state.current.videoId);
  }

  // The length limit: move on once a video has played that long.
  setInterval(function () {
    var max = state && Number(state.maxSeconds);
    if (!ready || !max || !state.current || state.current.id !== loadedId || !player.getCurrentTime) return;
    if (player.getPlayerState() === 1 && onCurrent() && player.getCurrentTime() >= max) report(state.current.id);
  }, 1000);

  window.addEventListener("onWidgetLoad", function (obj) {
    settings = obj.detail.fieldData || {};
    root = document.getElementById("media");
    editor = !!(obj.detail.overlay && obj.detail.overlay.isEditorMode);
    state = obj.detail.media || null;
    if (!state && editor) {
      state = { open: true, paused: false, volume: 0, maxSeconds: 0, command: "!sr", queueLength: 1,
        current: { id: "sample", videoId: "M7lc1UVf-VE", title: "YouTube Developers Live: Embedded Web Player Customization", user: "Cuebot" },
        queue: [{ id: "next", videoId: "", title: "A song someone asked for", user: "Ann" }] };
    }
    render();
    if (window.YT && window.YT.Player) createPlayer();
    else {
      window.onYouTubeIframeAPIReady = createPlayer;
      var script = document.createElement("script");
      script.src = "https://www.youtube.com/iframe_api";
      document.head.appendChild(script);
    }
  });

  window.addEventListener("onEventReceived", function (obj) {
    if (obj.detail.listener !== "cuebot-media" || !obj.detail.event) return;
    state = obj.detail.event;
    render();
  });
})();
