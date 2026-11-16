import { describe, expect, it } from "vitest";
import {
  EMPTY, EMPTY_QUEUES, MAX_LINES, actorText, applyEvents, applyQueueEvents, decidedHeld, findTool, formatDuration, hasQueues,
  heldStatusText, logDetail, logSummary, markHeld, mergeQueues, pendingHeld, presetLabel, safeUrl, shieldText, suspiciousText,
  termProblem, toolView, upsertRow, visibleLines, type ChatLine, type HeldItem, type LogRow, type PlatformTools, type QueueState,
  type Tool,
} from "../ui/pages/chat-model.js";

const line = (id: string, platform = "Twitch", text = "hi"): ChatLine => ({
  platform, id, user: "Bob", userId: "u1", text, time: "2026-11-16T20:00:00Z", roles: [], canDelete: true, deleted: false,
});

const row = (id: string, at: string, more: Partial<LogRow> = {}): LogRow => ({
  id, incidentId: id, at, actorKind: "Member", actorName: "Sam", platform: "Twitch", action: "ban", targetUserId: "u1",
  targetName: "Bob", messageText: "", reason: "", result: "Ok", error: "", redacted: false, ...more,
});

const tool = (more: Partial<Tool>): Tool => ({ action: "Timeout", label: "Timeout", state: "Works", note: "", allowed: true, ...more });

describe("chat events", () => {
  it("appends messages once and marks deletions on their line", () => {
    let s = applyEvents(EMPTY, [{ type: "message", message: line("1") }, { type: "message", message: line("2") }]);
    s = applyEvents(s, [{ type: "message", message: line("1") }]);
    expect(s.lines.map((l) => l.id)).toEqual(["1", "2"]);
    s = applyEvents(s, [{ type: "moderation", notice: { platform: "Twitch", kind: "deleted", messageId: "2", userId: "u1", user: "Bob", moderator: "", time: "" } }]);
    expect(s.lines.find((l) => l.id === "2")!.deleted).toBe(true);
    expect(s.lines.find((l) => l.id === "1")!.deleted).toBe(false);
    expect(s.notices).toHaveLength(1);
  });

  it("does not mark a line of another platform with the same id", () => {
    let s = applyEvents(EMPTY, [{ type: "message", message: line("7", "Kick") }]);
    s = applyEvents(s, [{ type: "moderation", notice: { platform: "Twitch", kind: "deleted", messageId: "7", userId: "", user: "", moderator: "", time: "" } }]);
    expect(s.lines[0]!.deleted).toBe(false);
  });

  it("keeps at most MAX_LINES lines and ignores unknown events", () => {
    const events = Array.from({ length: MAX_LINES + 20 }, (_, i) => ({ type: "message", message: line(String(i)) }));
    const s = applyEvents(EMPTY, [...events, { type: "other" }, null]);
    expect(s.lines).toHaveLength(MAX_LINES);
    expect(s.lines[0]!.id).toBe("20");
    expect(applyEvents(s, [{ type: "nothing" }])).toBe(s);
  });

  it("filters by platform without regard to case", () => {
    const lines = [line("1"), line("2", "YouTube")];
    expect(visibleLines(lines, "youtube").map((l) => l.id)).toEqual(["2"]);
    expect(visibleLines(lines, "")).toHaveLength(2);
  });

  it("keeps message text as data (the page renders it as text)", () => {
    const s = applyEvents(EMPTY, [{ type: "message", message: line("x", "Twitch", "<img src=x onerror=alert(1)>") }]);
    expect(s.lines[0]!.text).toBe("<img src=x onerror=alert(1)>");
  });
});

describe("tool states", () => {
  it("runs tools that work or do the closest thing, with the note and quota cost", () => {
    expect(toolView("Twitch", tool({})).kind).toBe("run");
    const kick = toolView("Kick", tool({ state: "Closest", note: "Kick counts whole minutes." }));
    expect(kick).toMatchObject({ kind: "run" });
    expect(kick.hint).toContain("Kick counts whole minutes.");
    expect(toolView("YouTube", tool({ quotaCost: 200 })).hint).toContain("200 units");
  });

  it("links website tools and greys out unavailable ones with the reason", () => {
    const web = toolView("Rumble", tool({ state: "OnWebsite", websiteUrl: "https://rumble.com/x", note: "No API." }));
    expect(web).toMatchObject({ kind: "link", url: "https://rumble.com/x", label: "Timeout (website)" });
    const off = toolView("YouTube", tool({ state: "NotAvailable", note: "YouTube has no warnings." }));
    expect(off).toMatchObject({ kind: "off" });
    expect(off.hint).toContain("YouTube has no warnings.");
  });

  it("greys out tools the role does not include, and missing ones", () => {
    expect(toolView("Twitch", tool({ allowed: false }))).toMatchObject({ kind: "off", hint: expect.stringContaining("role") });
    expect(toolView("Twitch", undefined, "Ban").kind).toBe("off");
  });

  it("finds a platform's tool and only opens https links", () => {
    const caps: PlatformTools[] = [{ platform: "Twitch", tools: [tool({ action: "Ban", label: "Ban" })], timeouts: [] }];
    expect(findTool(caps, "twitch", "Ban")?.label).toBe("Ban");
    expect(findTool(caps, "Kick", "Ban")).toBeUndefined();
    expect(safeUrl("javascript:alert(1)")).toBeUndefined();
    expect(safeUrl("http://example.com")).toBeUndefined();
    expect(safeUrl("not a url")).toBeUndefined();
    expect(safeUrl("https://kick.com/x")).toBe("https://kick.com/x");
  });
});

describe("durations", () => {
  it("formats like the desktop app", () => {
    expect(formatDuration(30)).toBe("30 s");
    expect(formatDuration(570)).toBe("9 min 30 s");
    expect(formatDuration(600)).toBe("10 min");
    expect(formatDuration(7200)).toBe("2 h");
    expect(formatDuration(86400)).toBe("1 day");
    expect(formatDuration(604800)).toBe("7 days");
  });

  it("shows the duration a platform really applies", () => {
    expect(presetLabel({ requested: 600, applied: 600, note: "" })).toBe("10 min");
    expect(presetLabel({ requested: 1209600, applied: 604800, note: "Kick: 7 days" })).toBe("14 days (applies 7 days)");
  });
});

describe("mod log", () => {
  it("says who did what", () => {
    expect(actorText(row("1", "2026"))).toBe("Sam (mod portal)");
    expect(actorText(row("1", "2026", { actorKind: "External", actorName: "Alex" }))).toBe("Alex (on Twitch)");
    expect(logSummary(row("1", "2026", { action: "timeout", appliedSeconds: 600 }))).toBe("timed out Bob for 10 min");
    expect(logSummary(row("1", "2026", { action: "mode", reason: "Slow mode on (30)" }))).toBe("Slow mode on (30)");
    expect(logSummary(row("1", "2026", { targetName: "", redacted: true }))).toContain("retention");
  });

  it("explains refusals and failures", () => {
    const refused = row("1", "2026", { result: "Skipped", error: "Sam has made 10 bans in the last 10 min.", reason: "spam" });
    expect(logDetail(refused)).toBe("Reason: spam · Not sent: Sam has made 10 bans in the last 10 min.");
    expect(logDetail(row("1", "2026", { result: "Failed", error: "401" }))).toBe("Failed: 401");
  });

  it("inserts new rows newest first and replaces changed ones", () => {
    let rows = [row("a", "2026-11-16T10:00:00Z")];
    rows = upsertRow(rows, row("b", "2026-11-16T11:00:00Z"));
    expect(rows.map((r) => r.id)).toEqual(["b", "a"]);
    rows = upsertRow(rows, row("a", "2026-11-16T10:00:00Z", { undoneAt: "2026-11-16T12:00:00Z" }));
    expect(rows).toHaveLength(2);
    expect(rows[1]!.undoneAt).toBeDefined();
  });
});

const held = (id: string, heldAt: string, more: Partial<HeldItem> = {}): HeldItem => ({
  platform: "Twitch", id, userId: "u1", user: "Bob", login: "bob", text: "held", reason: "AutoMod", heldAt, status: "Pending",
  decidedBy: "", ...more,
});

const queues = (items: HeldItem[]): QueueState => ({
  ...EMPTY_QUEUES,
  platforms: [{ platform: "Twitch", shield: { active: false, moderator: "" }, held: items, pendingHeld: items.filter((h) => h.status === "Pending").length }],
});

describe("held messages", () => {
  it("lists waiting messages oldest first and decided ones newest first, per platform", () => {
    const s = queues([held("b", "2026-11-16T20:02:00Z"), held("a", "2026-11-16T20:01:00Z"), held("c", "2026-11-16T20:00:00Z", { status: "Denied" }),
      held("d", "2026-11-16T20:03:00Z", { status: "Approved" })]);
    expect(pendingHeld(s, "").map((h) => h.id)).toEqual(["a", "b"]);
    expect(pendingHeld(s, "twitch")).toHaveLength(2);
    expect(pendingHeld(s, "Kick")).toEqual([]);
    expect(decidedHeld(s, "").map((h) => h.id)).toEqual(["d", "c"]);
  });

  it("adds held messages from push and replaces them when decided, with the platform's count", () => {
    let s = applyQueueEvents(EMPTY_QUEUES, [{ type: "held", platform: "Twitch", id: "h1", item: held("h1", "2026-11-16T20:00:00Z"), pendingHeld: 1 }]);
    expect(hasQueues(s)).toBe(true);
    expect(pendingHeld(s, "")).toHaveLength(1);
    s = applyQueueEvents(s, [{ type: "held", platform: "Twitch", id: "h1", item: held("h1", "2026-11-16T20:00:00Z", { status: "Approved", decidedBy: "Mia" }), pendingHeld: 0 }]);
    expect(pendingHeld(s, "")).toEqual([]);
    expect(s.platforms[0]!.held).toHaveLength(1);
    expect(s.platforms[0]!.pendingHeld).toBe(0);
    expect(heldStatusText(s.platforms[0]!.held![0]!)).toBe("Allowed by Mia");
  });

  it("marks a decision at once and ignores events without a platform", () => {
    const s = markHeld(queues([held("h1", "2026-11-16T20:00:00Z")]), "twitch", "h1", "Denied");
    expect(pendingHeld(s, "")).toEqual([]);
    expect(s.platforms[0]!.pendingHeld).toBe(0);
    expect(applyQueueEvents(s, [{ type: "held" }, null, "x"])).toBe(s);
  });

  it("keeps held text as data", () => {
    const s = applyQueueEvents(EMPTY_QUEUES, [{ type: "held", platform: "Twitch", item: held("x", "2026", { text: "<img src=x onerror=alert(1)>" }) }]);
    expect(pendingHeld(s, "")[0]!.text).toBe("<img src=x onerror=alert(1)>");
  });

  it("explains how a held message ended", () => {
    expect(heldStatusText(held("a", "", { status: "Denied" }))).toBe("Denied");
    expect(heldStatusText(held("a", "", { status: "Expired" }))).toContain("nobody decided");
  });
});

describe("shield mode and lists", () => {
  it("follows Shield Mode from push", () => {
    let s = applyQueueEvents(EMPTY_QUEUES, [{ type: "shield", platform: "Twitch", shield: { active: true, moderator: "Mia" } }]);
    expect(shieldText(s.platforms[0]!.shield)).toBe("Shield Mode is on (turned on by Mia)");
    s = applyQueueEvents(s, [{ type: "shield", platform: "twitch", shield: { active: false, moderator: "" } }]);
    expect(s.platforms).toHaveLength(1);
    expect(shieldText(s.platforms[0]!.shield)).toBe("Shield Mode is off");
    expect(shieldText(null)).toContain("not known");
  });

  it("asks for unban requests and terms again when push says they changed", () => {
    const s = applyQueueEvents(EMPTY_QUEUES, [{ type: "unbanRequests", platform: "Twitch" }, { type: "blockedTerms", platform: "Twitch" },
      { type: "blockedTerms", platform: "Twitch" }]);
    expect(s.unbanVersion).toBe(1);
    expect(s.termsVersion).toBe(2);
  });

  it("keeps lists a refresh did not include, and drops them when the platform failed", () => {
    const old = mergeQueues(EMPTY_QUEUES, [{ platform: "Twitch", shield: null, blockedTerms: [{ platform: "Twitch", id: "t1", text: "bad" }] }]);
    const plain = mergeQueues(old, [{ platform: "Twitch", shield: { active: true, moderator: "" } }]);
    expect(plain.platforms[0]!.blockedTerms).toHaveLength(1);
    expect(plain.platforms[0]!.shield?.active).toBe(true);
    const failed = mergeQueues(old, [{ platform: "Twitch", shield: null, blockedTermsError: "401" }]);
    expect(failed.platforms[0]!.blockedTerms).toBeUndefined();
  });

  it("checks a blocked term before sending it", () => {
    expect(termProblem("   ")).toContain("Type");
    expect(termProblem("x".repeat(501))).toContain("500");
    expect(termProblem(" spam ")).toBeNull();
  });

  it("describes suspicious chatters", () => {
    expect(suspiciousText(undefined)).toBe("");
    expect(suspiciousText({ level: "Restricted", user: "", why: "possible ban evader", moderator: "" }))
      .toBe("Restricted: only moderators see their messages (possible ban evader)");
    expect(suspiciousText({ level: "Monitored", user: "", why: "", moderator: "" })).toContain("flagged");
  });
});

describe("queue tools in the mod log", () => {
  it("words every queue action", () => {
    expect(logSummary(row("1", "2026", { action: "held-approve" }))).toBe("allowed a held message of Bob");
    expect(logSummary(row("1", "2026", { action: "suspicious-restrict" }))).toBe("restricted Bob");
    expect(logSummary(row("1", "2026", { action: "unban-request-deny" }))).toBe("denied Bob's unban request");
    expect(logSummary(row("1", "2026", { action: "term-add", reason: "spam", targetName: "" }))).toBe('blocked the term "spam"');
    expect(logSummary(row("1", "2026", { action: "shield", reason: "Shield Mode on" }))).toBe("Shield Mode on");
  });

  it("shows the answer to an unban request, and no reason line for terms", () => {
    expect(logDetail(row("1", "2026", { action: "unban-request-deny", reason: "Not this time." }))).toBe("Answer: Not this time.");
    expect(logDetail(row("1", "2026", { action: "term-remove", reason: "spam" }))).toBe("");
    expect(logDetail(row("1", "2026", { action: "shield", reason: "Shield Mode on" }))).toBe("");
  });
});
