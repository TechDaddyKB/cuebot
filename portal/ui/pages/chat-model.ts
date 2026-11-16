// The Chat page's logic without the page (portal/test/chat.test.ts tests it): chat lines, push events, tool states,
// timeout presets and mod log wording. Everything here is data; the page renders it as text, never as markup.

export interface ChatLine {
  platform: string;
  id: string;
  user: string;
  userId: string;
  text: string;
  time: string;
  roles: string[];
  canDelete: boolean;
  deleted: boolean;
  amount?: string;
}

/** A deletion, ban or timeout a platform reported, by anyone. */
export interface Notice {
  platform: string;
  kind: "deleted" | "banned" | "timedOut";
  time: string;
  messageId: string;
  userId: string;
  user: string;
  moderator: string;
  durationSeconds?: number;
}

export type ToolState = "Works" | "Closest" | "OnWebsite" | "NotAvailable";

/** One tool on one platform (ModerationCapability in src/Cuebot.Core/Chat/Moderation), plus what the role allows. */
export interface Tool {
  action: string;
  label: string;
  state: ToolState;
  note: string;
  allowed: boolean;
  minSeconds?: number;
  maxSeconds?: number;
  unitSeconds?: number;
  maxReason?: number;
  quotaCost?: number;
  websiteUrl?: string;
}

export interface TimeoutPreset { requested: number; applied: number; note: string }

export interface PlatformTools {
  platform: string;
  tools: Tool[];
  timeouts: TimeoutPreset[];
  quota?: { remaining: number; reserve: number; resetsAt: string };
}

export interface LogRow {
  id: string;
  incidentId: string;
  at: string;
  actorKind: "Streamer" | "Member" | "Automation" | "External";
  actorName: string;
  platform: string;
  action: string;
  targetUserId: string;
  targetName: string;
  messageText: string;
  reason: string;
  result: "Ok" | "Failed" | "Skipped" | "Manual";
  error: string;
  redacted: boolean;
  requestedSeconds?: number;
  appliedSeconds?: number;
  undoneAt?: string;
}

export const MAX_LINES = 500;

export interface ChatState {
  lines: ChatLine[];
  notices: Notice[];
}

export const EMPTY: ChatState = { lines: [], notices: [] };

/** Applies "chat" push events in order: new lines (at most MAX_LINES kept), and deletions marked on their line. */
export function applyEvents(state: ChatState, events: unknown[]): ChatState {
  let lines = state.lines;
  let notices = state.notices;
  for (const raw of events) {
    if (!raw || typeof raw !== "object") continue;
    const e = raw as { type?: string; message?: ChatLine; notice?: Notice };
    if (e.type === "message" && e.message && typeof e.message.id === "string") {
      if (lines.some((l) => l.platform === e.message!.platform && l.id === e.message!.id)) continue;
      lines = [...lines, e.message];
    } else if (e.type === "moderation" && e.notice) {
      const n = e.notice;
      notices = [...notices, n].slice(-100);
      if (n.kind === "deleted" && n.messageId) {
        lines = lines.map((l) => (l.platform === n.platform && l.id === n.messageId && !l.deleted ? { ...l, deleted: true } : l));
      }
    }
  }
  if (lines.length > MAX_LINES) lines = lines.slice(-MAX_LINES);
  return lines === state.lines && notices === state.notices ? state : { lines, notices };
}

/** The lines of one platform ("" for all). */
export function visibleLines(lines: ChatLine[], platform: string): ChatLine[] {
  return platform ? lines.filter((l) => l.platform.toLowerCase() === platform.toLowerCase()) : lines;
}

/** Inserts or replaces a log row (an unban marks the ban undone: the same row arrives again), newest first. */
export function upsertRow(rows: LogRow[], row: LogRow): LogRow[] {
  const i = rows.findIndex((r) => r.id === row.id);
  if (i >= 0) return rows.map((r, n) => (n === i ? row : r));
  return [row, ...rows].sort((a, b) => b.at.localeCompare(a.at));
}

/** What a button for a tool does: run it, open the platform's page, or nothing (greyed, with the reason). */
export interface ToolView {
  kind: "run" | "link" | "off";
  label: string;
  /** The tooltip and the line under a greyed button. */
  hint: string;
  url?: string;
}

export function toolView(platform: string, tool: Tool | undefined, label?: string): ToolView {
  const name = label ?? tool?.label ?? "This";
  if (!tool) return { kind: "off", label: name, hint: `${platform}: Cuebot cannot do this here.` };
  if (!tool.allowed) return { kind: "off", label: name, hint: "Your role does not include this. Ask the streamer." };
  const cost = tool.quotaCost ? ` Costs ${tool.quotaCost.toLocaleString("en")} units of YouTube's daily quota.` : "";
  switch (tool.state) {
    case "Works":
      return { kind: "run", label: name, hint: `${platform}: works.${cost}` };
    case "Closest":
      return { kind: "run", label: name, hint: `${platform}, closest equivalent: ${tool.note || "not exactly the same."}${cost}` };
    case "OnWebsite":
      return tool.websiteUrl
        ? { kind: "link", label: `${name} (website)`, hint: `${platform}: done on the website. ${tool.note}`.trim(), url: tool.websiteUrl }
        : { kind: "off", label: name, hint: `${platform}: done on the website. ${tool.note}`.trim() };
    default:
      return { kind: "off", label: name, hint: `${platform}: ${tool.note || "not available."}` };
  }
}

export function findTool(caps: PlatformTools[], platform: string, action: string): Tool | undefined {
  return caps.find((c) => c.platform.toLowerCase() === platform.toLowerCase())?.tools.find((t) => t.action === action);
}

/** "30 s", "9 min 30 s", "2 h", "7 days" (as ModerationService.FormatDuration). */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.ceil(seconds));
  if (s >= 86400 && s % 86400 === 0) return s === 86400 ? "1 day" : `${s / 86400} days`;
  if (s >= 3600 && s % 3600 === 0) return `${s / 3600} h`;
  if (s >= 60) {
    const minutes = Math.floor(s / 60);
    const rest = s % 60;
    return rest > 0 ? `${minutes} min ${rest} s` : `${minutes} min`;
  }
  return `${s} s`;
}

/** A timeout choice as the button shows it: "10 min", or "1 min → Kick: 1 min" when the platform applies something else. */
export function presetLabel(p: TimeoutPreset): string {
  return p.applied === p.requested ? formatDuration(p.requested) : `${formatDuration(p.requested)} (applies ${formatDuration(p.applied)})`;
}

/** "Sam (mod portal)", "the streamer", "Shoutout (automatic)", "Sam (on Twitch)". */
export function actorText(row: LogRow): string {
  switch (row.actorKind) {
    case "Streamer": return "The streamer";
    case "Member": return `${row.actorName} (mod portal)`;
    case "Automation": return `${row.actorName} (automatic)`;
    default: return `${row.actorName} (on ${row.platform})`;
  }
}

/** "banned Bob", "timed out Bob for 10 min", "deleted a message of Bob", "Slow mode on". */
export function logSummary(row: LogRow): string {
  const who = row.targetName || (row.redacted ? "(removed after the retention period)" : "someone");
  const seconds = row.appliedSeconds ?? row.requestedSeconds;
  switch (row.action) {
    case "ban": return `banned ${who}`;
    case "timeout": return `timed out ${who}${seconds ? ` for ${formatDuration(seconds)}` : ""}`;
    case "unban": return `lifted the ban of ${who}`;
    case "delete": return `deleted a message of ${who}`;
    case "warn": return `warned ${who}`;
    case "clear": return "cleared the chat";
    case "pin": return "pinned a message";
    case "unpin": return "unpinned a message";
    case "mode": return row.reason || "changed a chat mode";
    case "shield": return row.reason || "changed Shield Mode";
    case "held-approve": return `allowed a held message of ${who}`;
    case "held-deny": return `denied a held message of ${who}`;
    case "suspicious-monitor": return `marked ${who} as monitored`;
    case "suspicious-restrict": return `restricted ${who}`;
    case "suspicious-clear": return `cleared ${who}'s suspicious status`;
    case "unban-request-approve": return `approved ${who}'s unban request`;
    case "unban-request-deny": return `denied ${who}'s unban request`;
    case "term-add": return row.reason ? `blocked the term "${row.reason}"` : "blocked a term";
    case "term-remove": return row.reason ? `unblocked the term "${row.reason}"` : "unblocked a term";
    default: return `${row.action} ${who}`;
  }
}

/** The reason, the deleted message, and how it ended when it did not work. */
export function logDetail(row: LogRow): string {
  const parts: string[] = [];
  // Chat modes, Shield Mode and terms keep what changed in the reason (logSummary shows it).
  const reasonInSummary = row.action === "mode" || row.action === "shield" || row.action.startsWith("term-");
  if (!reasonInSummary && row.reason) parts.push(`${row.action.startsWith("unban-request-") ? "Answer" : "Reason"}: ${row.reason}`);
  if (row.messageText) parts.push(`Message: ${row.messageText}`);
  if (row.result === "Failed") parts.push(`Failed: ${row.error}`);
  else if (row.result === "Skipped") parts.push(`Not sent: ${row.error}`);
  else if (row.result === "Manual") parts.push("Done by hand");
  if (row.undoneAt) parts.push(`Lifted ${new Date(row.undoneAt).toLocaleString()}`);
  return parts.join(" · ");
}

/** The chat modes the toolbar offers, in order. */
export const MODE_ACTIONS = ["Clear", "SlowMode", "FollowersOnly", "SubscribersOnly", "EmoteOnly"] as const;

/** Whether a link a platform gave is safe to open: https only (no javascript:, data: or the like). */
export function safeUrl(url: string | undefined): string | undefined {
  if (!url) return undefined;
  try {
    return new URL(url).protocol === "https:" ? url : undefined;
  } catch {
    return undefined;
  }
}

/** Short platform tags for the chat list. */
export function platformClass(platform: string): string {
  return `platform-${platform.toLowerCase().replace(/[^a-z0-9]/g, "")}`;
}

// ---------------------------------------------------------------- moderation queues (phase 3, Twitch)

export type HeldStatus = "Pending" | "Approved" | "Denied" | "Expired";

/** A message the platform's filter held for a moderator (HeldChatMessage). Text only. */
export interface HeldItem {
  platform: string;
  id: string;
  userId: string;
  user: string;
  login: string;
  text: string;
  reason: string;
  heldAt: string;
  status: HeldStatus;
  decidedBy: string;
}

export interface ShieldState { active: boolean; moderator: string; since?: string }

export interface UnbanRequest {
  platform: string;
  id: string;
  userId: string;
  user: string;
  login: string;
  text: string;
  createdAt: string;
  status: "Pending" | "Approved" | "Denied" | "Closed";
  resolvedBy: string;
  resolutionText: string;
}

export interface BlockedTerm { platform: string; id: string; text: string }

export interface Suspicious { level: "Monitored" | "Restricted"; user: string; why: string; moderator: string }

/** One platform's queues as chat.queues answers them; fields are missing when the role does not include them. */
export interface PlatformQueues {
  platform: string;
  shield: ShieldState | null;
  held?: HeldItem[];
  pendingHeld?: number;
  unbanRequests?: UnbanRequest[];
  unbanRequestsError?: string;
  blockedTerms?: BlockedTerm[];
  blockedTermsError?: string;
}

export interface QueueState {
  platforms: PlatformQueues[];
  /** Bumped by a push event that says the list changed and must be read again. */
  unbanVersion: number;
  termsVersion: number;
}

export const EMPTY_QUEUES: QueueState = { platforms: [], unbanVersion: 0, termsVersion: 0 };

const samePlatform = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

function updatePlatform(platforms: PlatformQueues[], platform: string, change: (q: PlatformQueues) => PlatformQueues): PlatformQueues[] {
  const i = platforms.findIndex((q) => samePlatform(q.platform, platform));
  if (i < 0) return [...platforms, change({ platform, shield: null })];
  return platforms.map((q, n) => (n === i ? change(q) : q));
}

/** Applies "modqueue" push events: held messages added or decided, Shield Mode, and lists that must be read again. */
export function applyQueueEvents(state: QueueState, events: unknown[]): QueueState {
  let s = state;
  for (const raw of events) {
    if (!raw || typeof raw !== "object") continue;
    const e = raw as { type?: string; platform?: string; id?: string; item?: HeldItem; pendingHeld?: number; shield?: ShieldState | null };
    if (typeof e.platform !== "string") continue;
    const platform = e.platform;
    switch (e.type) {
      case "held":
        s = {
          ...s,
          platforms: updatePlatform(s.platforms, platform, (q) => {
            const held = q.held ?? [];
            const item = e.item;
            const next = item ? [...held.filter((h) => h.id !== item.id), item] : held;
            return { ...q, held: next, pendingHeld: typeof e.pendingHeld === "number" ? e.pendingHeld : next.filter((h) => h.status === "Pending").length };
          }),
        };
        break;
      case "shield":
        s = { ...s, platforms: updatePlatform(s.platforms, platform, (q) => ({ ...q, shield: e.shield ?? null })) };
        break;
      case "unbanRequests":
        s = { ...s, unbanVersion: s.unbanVersion + 1 };
        break;
      case "blockedTerms":
        s = { ...s, termsVersion: s.termsVersion + 1 };
        break;
    }
  }
  return s;
}

/** Replaces each platform's queues with a fresh chat.queues answer, keeping lists the answer did not include. */
export function mergeQueues(state: QueueState, fresh: PlatformQueues[]): QueueState {
  const platforms = fresh.map((f) => {
    const old = state.platforms.find((q) => samePlatform(q.platform, f.platform));
    return {
      ...f,
      unbanRequests: f.unbanRequests ?? (f.unbanRequestsError ? undefined : old?.unbanRequests),
      blockedTerms: f.blockedTerms ?? (f.blockedTermsError ? undefined : old?.blockedTerms),
    };
  });
  return { ...state, platforms };
}

/** Held messages waiting for a decision (one platform, or "" for all), oldest first. */
export function pendingHeld(state: QueueState, platform: string): HeldItem[] {
  return state.platforms
    .filter((q) => !platform || samePlatform(q.platform, platform))
    .flatMap((q) => q.held ?? [])
    .filter((h) => h.status === "Pending")
    .sort((a, b) => a.heldAt.localeCompare(b.heldAt));
}

/** Held messages decided lately, newest first (shown under the waiting ones). */
export function decidedHeld(state: QueueState, platform: string, max = 10): HeldItem[] {
  return state.platforms
    .filter((q) => !platform || samePlatform(q.platform, platform))
    .flatMap((q) => q.held ?? [])
    .filter((h) => h.status !== "Pending")
    .sort((a, b) => b.heldAt.localeCompare(a.heldAt))
    .slice(0, max);
}

/** Removes a held message from the waiting list at once after a mod decided (the push confirms it). */
export function markHeld(state: QueueState, platform: string, id: string, status: HeldStatus): QueueState {
  return {
    ...state,
    platforms: updatePlatform(state.platforms, platform, (q) => {
      const held = (q.held ?? []).map((h) => (h.id === id ? { ...h, status } : h));
      return { ...q, held, pendingHeld: held.filter((h) => h.status === "Pending").length };
    }),
  };
}

/** "Allowed by Mia", "Denied", "Dropped by Twitch (nobody decided in time)". */
export function heldStatusText(h: HeldItem): string {
  const by = h.decidedBy ? ` by ${h.decidedBy}` : "";
  switch (h.status) {
    case "Approved": return `Allowed${by}`;
    case "Denied": return `Denied${by}`;
    case "Expired": return `Dropped by ${h.platform} (nobody decided in time)`;
    default: return "Waiting";
  }
}

/** "Shield Mode is on (turned on by Mia)", "Shield Mode is off", "Shield Mode: not known yet". */
export function shieldText(shield: ShieldState | null | undefined): string {
  if (!shield) return "Shield Mode: not known yet";
  const by = shield.moderator ? ` (turned ${shield.active ? "on" : "off"} by ${shield.moderator})` : "";
  return `Shield Mode is ${shield.active ? "on" : "off"}${by}`;
}

/** "Monitored: possible ban evader", "Restricted", or "" when not flagged. */
export function suspiciousText(s: Suspicious | undefined | null): string {
  if (!s) return "";
  const what = s.level === "Restricted" ? "Restricted: only moderators see their messages" : "Monitored: their messages are flagged for moderators";
  return s.why ? `${what} (${s.why})` : what;
}

export const MAX_TERM = 500;

/** Why a blocked term cannot be sent, or null when it can (Cuebot checks again). */
export function termProblem(term: string): string | null {
  const t = term.trim();
  if (!t) return "Type the word or phrase to block.";
  if (t.length > MAX_TERM) return `Keep the term under ${MAX_TERM} characters.`;
  return null;
}

/** Whether any platform has moderation queues (only those appear in chat.queues). */
export function hasQueues(state: QueueState): boolean {
  return state.platforms.length > 0;
}
