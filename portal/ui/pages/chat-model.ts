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
    default: return `${row.action} ${who}`;
  }
}

/** The reason, the deleted message, and how it ended when it did not work. */
export function logDetail(row: LogRow): string {
  const parts: string[] = [];
  if (row.action !== "mode" && row.reason) parts.push(`Reason: ${row.reason}`);
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
