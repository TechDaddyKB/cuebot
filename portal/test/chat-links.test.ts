import { describe, expect, it } from "vitest";
import {
  EMPTY_LINKS, accountButtons, applyLinkEvents, byHandRows, canUndoIncident, countdown, executeProblem, groupIncidents, logDetail,
  logSummary, needsSecondConfirmation, pickable, quotaTotal, resultText, secondConfirmationText, selectedRows, toggleRow,
  type LinkRights, type LinkedAccount, type LinkedPreview, type LogRow, type PreviewRow,
} from "../ui/pages/chat-model.js";

const row = (rowId: string, platform: string, more: Partial<PreviewRow> = {}): PreviewRow => ({
  rowId, platform, userId: rowId, name: rowId, isOrigin: rowId === "origin", status: rowId === "origin" ? null : "Confirmed",
  linkText: "", state: "Works", willHappen: "Ban", quotaCost: 0, notes: "", ticked: true, canTick: true, byHand: false,
  alreadyActioned: false, ...more,
});

const preview = (rows: PreviewRow[], action: "Ban" | "Timeout" = "Ban"): LinkedPreview => ({
  previewId: "p", action, platform: "Twitch", name: "rowdy", linked: rows.length > 1, rows,
  defaultTicked: rows.filter((r) => r.ticked).map((r) => r.rowId), maxReason: 500,
});

const linkedRows = [
  row("origin", "Twitch"),
  row("kick", "Kick"),
  row("yt", "YouTube", { status: "Suggested", ticked: false, quotaCost: 200, notes: "Unticked: not confirmed" }),
  row("rumble", "Rumble", { byHand: true, canTick: false, ticked: false, state: "OnWebsite", websiteUrl: "https://rumble.com" }),
  row("twitch-alt", "Twitch", { status: "Suggested", ticked: false, canTick: false, notes: "A linked ban needs a confirmed or verified link" }),
];

describe("linked preview", () => {
  it("counts only rows that may be ticked", () => {
    const p = preview(linkedRows);
    const ticked = new Set(["origin", "kick", "twitch-alt", "rumble"]);
    expect(selectedRows(p, ticked).map((r) => r.rowId)).toEqual(["origin", "kick"]);
    expect(byHandRows(p).map((r) => r.rowId)).toEqual(["rumble"]);
  });

  it("asks a second time for a permanent ban on more than one platform", () => {
    const p = preview(linkedRows);
    expect(needsSecondConfirmation(p, new Set(["origin"]))).toBe(false);
    expect(needsSecondConfirmation(p, new Set(["origin", "kick"]))).toBe(true);
    expect(secondConfirmationText(p, new Set(["origin", "kick", "yt"]))).toBe("Ban 3 accounts on 3 platforms?");
    expect(secondConfirmationText(p, new Set(["origin"]))).toBe("Ban 1 account on 1 platform?");
    expect(needsSecondConfirmation(preview(linkedRows, "Timeout"), new Set(["origin", "kick"]))).toBe(false);
  });

  it("requires a reason and something to do", () => {
    const p = preview(linkedRows);
    expect(executeProblem(p, new Set(["origin"]), "  ")).toBe("A reason is required.");
    expect(executeProblem(p, new Set(["origin"]), "x".repeat(501))).toContain("under 500");
    expect(executeProblem(p, new Set(["origin"]), "evading")).toBeNull();
    expect(executeProblem(preview([row("origin", "Twitch")]), new Set(), "evading")).toBe("Nothing is ticked.");
    // A by-hand row through a confirmed link still comes back to be done by hand.
    expect(executeProblem(p, new Set(), "evading")).toBeNull();
  });

  it("ticks and unticks only rows that allow it, and adds up the quota", () => {
    const p = preview(linkedRows);
    let ticked = new Set(p.defaultTicked);
    expect([...ticked]).toEqual(["origin", "kick"]);
    ticked = toggleRow(p, ticked, "yt");
    expect(ticked.has("yt")).toBe(true);
    expect(quotaTotal(p, ticked)).toBe(200);
    expect(toggleRow(p, ticked, "twitch-alt").has("twitch-alt")).toBe(false);
    expect(toggleRow(p, ticked, "rumble").has("rumble")).toBe(false);
    expect(toggleRow(p, ticked, "kick").has("kick")).toBe(false);
  });

  it("words each result", () => {
    const base = { rowId: "r", platform: "Kick", name: "x", message: "Kick said no", awaitingManual: false, canRetry: false };
    expect(resultText({ ...base, result: "Ok" })).toBe("Done");
    expect(resultText({ ...base, result: "Failed", canRetry: true })).toBe("Failed: Kick said no");
    expect(resultText({ ...base, result: "Skipped", awaitingManual: true, platform: "Rumble" })).toContain("By hand: open Rumble");
    expect(resultText({ ...base, result: "Skipped" })).toBe("Not sent: Kick said no");
    expect(resultText({ ...base, result: "Manual" })).toBe("Done by hand");
  });
});

const logRow = (id: string, incidentId: string, more: Partial<LogRow> = {}): LogRow => ({
  id, incidentId, at: "2026-11-16T20:00:00Z", actorKind: "Member", actorName: "Sam", platform: "Twitch", action: "ban", targetUserId: "u1",
  targetName: "Bob", messageText: "", reason: "", result: "Ok", error: "", redacted: false, ...more,
});

describe("incidents in the mod log", () => {
  it("groups neighbouring rows of one incident and offers undo while a ban is in force", () => {
    const groups = groupIncidents([
      logRow("1", "a"), logRow("2", "a", { platform: "Kick", linked: true }), logRow("3", "b"), logRow("4", "a"),
    ]);
    expect(groups.map((g) => g.rows.map((r) => r.id))).toEqual([["1", "2"], ["3"], ["4"]]);
    expect(canUndoIncident(groups[0]!)).toBe(true);
    expect(canUndoIncident(groups[1]!)).toBe(false);
    const lifted = groupIncidents([logRow("1", "a", { undoneAt: "x" }), logRow("2", "a", { result: "Failed", linked: true })]);
    expect(canUndoIncident(lifted[0]!)).toBe(false);
    expect(canUndoIncident(groupIncidents([logRow("5", "c", { linked: true, result: "Manual" })])[0]!)).toBe(true);
  });

  it("words link rows and rows made through a link", () => {
    expect(logSummary(logRow("1", "a", { action: "link-confirm" }))).toBe("confirmed the link of Bob");
    expect(logSummary(logRow("1", "a", { action: "unlink" }))).toBe("unlinked Bob");
    expect(logDetail(logRow("1", "a", { linked: true, reason: "spam" }))).toBe("Reason: spam · Through a linked account");
  });
});

const rights = (more: Partial<LinkRights> = {}): LinkRights => ({
  view: true, propose: true, confirm: false, notesRead: true, notesAdd: true, notesDelete: false, ban: true, timeout: true,
  linkedBans: true, linkedTimeouts: true, ...more,
});

const account = (more: Partial<LinkedAccount> = {}): LinkedAccount => ({
  linkId: "l", platform: "Kick", userId: "k1", name: "rowdy", nameAtLink: "rowdy", isThis: false, status: "Suggested", between: "Suggested",
  linkText: "suggested by Sam, not confirmed", addedBy: "Sam", addedAt: "", statusAt: "", mine: false, needsStreamer: false, confirmedBy: [], ...more,
});

describe("linked accounts on the user card", () => {
  it("never offers Confirm on your own suggestion", () => {
    expect(accountButtons(account({ mine: true }), rights({ confirm: true }))).toMatchObject({ confirm: false, wrong: true, confirmHint: expect.stringContaining("someone else") });
    expect(accountButtons(account(), rights())).toMatchObject({ confirm: false, wrong: false, confirmHint: expect.stringContaining("role") });
    expect(accountButtons(account(), rights({ confirm: true }))).toMatchObject({ confirm: true, wrong: true });
    expect(accountButtons(account({ needsStreamer: true }), rights({ confirm: true })).confirm).toBe(false);
    expect(accountButtons(account({ status: "Confirmed" }), rights()).unlink).toBe(false);
    expect(accountButtons(account({ status: "Confirmed" }), rights({ confirm: true })).unlink).toBe(true);
  });

  it("filters the people a mod can pick, hints first, without duplicates", () => {
    const hints = [{ platform: "Kick", userId: "k1", name: "rowdyguy" }];
    const recent = [{ platform: "Kick", userId: "k1", name: "rowdyguy" }, { platform: "YouTube", userId: "UC1", name: "Someone" }];
    expect(pickable(hints, recent, "").map((c) => c.userId)).toEqual(["k1", "UC1"]);
    expect(pickable(hints, recent, "some").map((c) => c.userId)).toEqual(["UC1"]);
    expect(pickable(hints, recent, "uc1").map((c) => c.userId)).toEqual(["UC1"]);
  });

  it("counts the !link code down", () => {
    const now = Date.parse("2026-11-16T20:00:00Z");
    expect(countdown("2026-11-16T20:10:00Z", now)).toBe("10:00");
    expect(countdown("2026-11-16T20:00:05Z", now)).toBe("0:05");
    expect(countdown("2026-11-16T19:59:00Z", now)).toBe("expired");
    expect(countdown("not a date", now)).toBe("expired");
  });

  it("applies links push events", () => {
    let s = applyLinkEvents(EMPTY_LINKS, [{ type: "changed", kind: "proposed" }, { nonsense: true }, null]);
    expect(s.version).toBe(1);
    s = applyLinkEvents(s, [{ type: "later", items: [{ id: "x", platform: "Kick", userId: "k1", name: "r", bannedOn: ["Twitch"], at: "", text: "t" }] }]);
    expect(s.later).toHaveLength(1);
    expect(s.version).toBe(2);
  });
});
