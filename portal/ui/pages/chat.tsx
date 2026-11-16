// The combined chat (docs/UNIFIED_MOD_CHAT_PLAN.md, phase 2): every platform's chat in one list, the moderation tools
// as each platform offers them, the mod log and a user card. Chat is rendered as text only. Moderation applies at once
// (it never waits for approval) and Cuebot's mod log records who did it. Cuebot re-checks every request.
// Phase 3 adds the platforms' moderation queues (Twitch): a Held lane above the chat, a Mod tools tab (Shield Mode,
// unban requests, blocked terms) and Monitor / Restrict / Not suspicious on the user card.

import { useEffect, useRef, useState } from "preact/hooks";
import { useSubscription, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";
import {
  EMPTY, EMPTY_QUEUES, MAX_TERM, MODE_ACTIONS, actorText, applyEvents, applyQueueEvents, decidedHeld, findTool, formatDuration,
  hasQueues, heldStatusText, logDetail, logSummary, markHeld, mergeQueues, pendingHeld, platformClass, presetLabel, safeUrl,
  shieldText, suspiciousText, termProblem, toolView, upsertRow, visibleLines, type ChatLine, type ChatState, type HeldItem,
  type LogRow, type PlatformQueues, type PlatformTools, type QueueState, type Suspicious, type ToolView,
} from "./chat-model.js";

interface History extends Answer { platforms?: string[]; messages?: ChatLine[] }
interface Caps extends Answer { platforms?: PlatformTools[] }
interface LogPage extends Answer { rows?: LogRow[]; more?: boolean }
interface UserCard extends Answer {
  platform: string; userId: string; name: string; roles: string[]; messages: ChatLine[]; log?: LogRow[]; inForce?: LogRow;
  suspiciousKnown?: boolean; suspicious?: Suspicious;
}
interface Queues extends Answer { platforms?: PlatformQueues[] }
interface Target { platform: string; userId: string; user: string; messageId?: string }

const POLL_MS = 3000;
const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

/** Opens the chat on its own, full height, for a second monitor. The new window resumes this device's sign-in. */
function popOut() {
  window.open(`${location.origin}${location.pathname}#chat-popout`, "cuebot-chat", "popup,width=480,height=900");
}

export function ChatPage({ popout = false }: { popout?: boolean }) {
  const { host, can, call, notify } = usePortal();
  const [chat, setChat] = useState<ChatState>(EMPTY);
  const [platforms, setPlatforms] = useState<string[]>([]);
  const [problem, setProblem] = useState<History | null>(null);
  const [caps, setCaps] = useState<PlatformTools[]>([]);
  const [filter, setFilter] = useState("");
  const [tab, setTab] = useState<"chat" | "tools" | "log">("chat");
  const [queues, setQueues] = useState<QueueState>(EMPTY_QUEUES);
  const [selected, setSelected] = useState<string | null>(null);
  const [card, setCard] = useState<Target | null>(null);
  const [pushEnded, setPushEnded] = useState(false);
  const list = useRef<HTMLOListElement>(null);
  const stick = useRef(true);

  const loadHistory = async () => {
    const a = (await host.request("chat.history").catch((e: unknown) => ({ ok: false, error: String(e) }))) as History;
    setProblem(a.ok ? null : a);
    if (!a.ok) return;
    setPlatforms(a.platforms ?? []);
    setChat((s) => ({ lines: a.messages ?? [], notices: s.notices }));
  };
  const loadCaps = async () => {
    const a = (await host.request("chat.capabilities").catch(() => ({ ok: false }))) as Caps;
    if (a.ok) setCaps(a.platforms ?? []);
  };

  const loadQueues = async () => {
    const a = (await host.request("chat.queues").catch(() => ({ ok: false }))) as Queues;
    if (a.ok) setQueues((s) => mergeQueues(s, a.platforms ?? []));
  };
  const queuesPushing = useSubscription("modqueue", {}, {
    onEvents: (events) => setQueues((s) => applyQueueEvents(s, events)),
    onResync: () => void loadQueues(),
  });
  useEffect(() => {
    void loadQueues();
    const poll = setInterval(() => void loadQueues(), queuesPushing ? 120000 : 15000);   // Shield Mode set elsewhere, expired holds
    return () => clearInterval(poll);
  }, [queuesPushing]);

  const pushing = useSubscription("chat", {}, {
    onEvents: (events) => setChat((s) => applyEvents(s, events)),
    onResync: () => void loadHistory(),
    onEnd: () => setPushEnded(true),
  });
  useEffect(() => {
    void loadHistory();
    void loadCaps();
    const caps = setInterval(() => void loadCaps(), 60000);   // the YouTube quota and connections change
    const poll = !pushing || pushEnded ? setInterval(() => void loadHistory(), POLL_MS) : undefined;
    return () => {
      clearInterval(caps);
      if (poll) clearInterval(poll);
    };
  }, [pushing, pushEnded]);

  // Keep the newest line in view unless the mod scrolled up to read.
  useEffect(() => {
    const el = list.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [chat.lines, filter, tab]);

  const act = async (action: string, platform: string, body: Record<string, unknown>) => {
    const answer = await call("chat.act", { action, platform, ...body });
    if (answer.ok) notify(String(answer.message ?? "Done."));
    return answer.ok;
  };

  const lines = visibleLines(chat.lines, filter);
  const shownPlatforms = filter ? [filter] : platforms;
  const showTools = hasQueues(queues) && (can("ChatModes") || can("ChatBan") || can("BlockedTerms"));
  const waiting = pendingHeld(queues, filter);
  const decide = async (h: HeldItem, allow: boolean) => {
    if (await act(allow ? "ApproveHeld" : "DenyHeld", h.platform, { itemId: h.id }))
      setQueues((s) => markHeld(s, h.platform, h.id, allow ? "Approved" : "Denied"));
  };

  return (
    <div class={popout ? "chat-page popout" : "chat-page"}>
      <div class="page-head">
        {!popout && <h1>Chat</h1>}
        <div class="chat-controls">
          <label class="sr-only" for="chat-platform">Platform</label>
          <select id="chat-platform" value={filter} onChange={(e) => setFilter((e.target as HTMLSelectElement).value)}>
            <option value="">All platforms</option>
            {platforms.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
          {(can("ViewModLog") || showTools) && (
            <span class="tabs" role="tablist">
              <button type="button" role="tab" aria-selected={tab === "chat"} class={tab === "chat" ? "active" : ""} onClick={() => setTab("chat")}>Chat</button>
              {showTools && <button type="button" role="tab" aria-selected={tab === "tools"} class={tab === "tools" ? "active" : ""} onClick={() => setTab("tools")}>Mod tools</button>}
              {can("ViewModLog") && <button type="button" role="tab" aria-selected={tab === "log"} class={tab === "log" ? "active" : ""} onClick={() => setTab("log")}>Mod log</button>}
            </span>
          )}
          {!popout && <button type="button" onClick={popOut} title="Chat only, full height, in its own window">Pop out</button>}
        </div>
      </div>
      <LoadError data={problem} />
      {(!pushing || pushEnded) && <p class="muted small">Live updates are not available; the chat refreshes every few seconds.</p>}

      {tab === "chat" && can("ChatModes") && (
        <ModeBar platforms={shownPlatforms} caps={caps} act={act} />
      )}

      {tab === "chat" && hasQueues(queues) && can("ChatDeleteTimeout") && (
        <HeldLane waiting={waiting} decided={decidedHeld(queues, filter)} caps={caps} decide={decide}
          onCard={(h) => setCard({ platform: h.platform, userId: h.userId, user: h.user })} />
      )}

      {tab === "tools" ? (
        <ModTools queues={queues} platform={filter} caps={caps} act={act} reload={() => void loadQueues()} />
      ) : tab === "chat" ? (
        <ol class="chat-lines" ref={list} aria-live="polite"
          onScroll={(e) => { const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40; }}>
          {lines.length === 0 && <li class="muted">No chat yet.</li>}
          {lines.map((l) => {
            const key = `${l.platform}|${l.id}`;
            return (
              <li key={key} class={`chat-line${l.deleted ? " deleted" : ""}${selected === key ? " selected" : ""}`}>
                <div class="chat-row" onClick={() => setSelected(selected === key ? null : key)}>
                  <time class="muted">{time(l.time)}</time>
                  <span class={`chip ${platformClass(l.platform)}`}>{l.platform}</span>
                  <button type="button" class="link chat-user" title={`${l.platform} user id ${l.userId}`}
                    onClick={(e) => { e.stopPropagation(); setCard({ platform: l.platform, userId: l.userId, user: l.user }); }}>{l.user}</button>
                  {l.roles.filter((r) => r !== "Subscriber" && r !== "Verified").map((r) => <span key={r} class="chip role">{r}</span>)}
                  {l.amount && <span class="chip amount">{l.amount}</span>}
                  <span class="chat-text">{l.deleted ? "(deleted) " : ""}{l.text}</span>
                </div>
                {selected === key && (
                  <Actions target={{ platform: l.platform, userId: l.userId, user: l.user, messageId: l.id }} line={l} caps={caps} act={act}
                    onCard={() => setCard({ platform: l.platform, userId: l.userId, user: l.user })} />
                )}
              </li>
            );
          })}
        </ol>
      ) : <ModLog platform={filter} />}

      {card && <UserCardView target={card} caps={caps} act={act} onClose={() => setCard(null)} />}
    </div>
  );
}

type Act = (action: string, platform: string, body: Record<string, unknown>) => Promise<boolean>;

/** One tool as a button, a link to the platform's page, or greyed with the reason. */
function ToolButton({ view, onRun, primary, danger }: { view: ToolView; onRun: () => void; primary?: boolean; danger?: boolean }) {
  if (view.kind === "link") {
    const url = safeUrl(view.url);
    return url
      ? <a class="button" href={url} target="_blank" rel="noopener noreferrer" title={view.hint}>{view.label}</a>
      : <button type="button" disabled title={view.hint}>{view.label}</button>;
  }
  return (
    <button type="button" class={danger ? "danger" : primary ? "primary" : ""} disabled={view.kind === "off"} title={view.hint}
      onClick={onRun}>{view.label}</button>
  );
}

/** The tools for one message or person: delete, timeout (with what each platform applies), ban, unban, warn. */
function Actions({ target, line, caps, act, onCard }: { target: Target; line?: ChatLine; caps: PlatformTools[]; act: Act; onCard?: () => void }) {
  const p = target.platform;
  const platformCaps = caps.find((c) => c.platform.toLowerCase() === p.toLowerCase());
  const presets = platformCaps?.timeouts ?? [];
  const [preset, setPreset] = useState(600);
  const [reason, setReason] = useState("");
  const chosen = presets.find((x) => x.requested === preset);
  const who = { userId: target.userId, user: target.user, messageId: target.messageId };
  const view = (action: string) => toolView(p, findTool(caps, p, action));
  const del = line && !line.canDelete
    ? { kind: "off" as const, label: "Delete message", hint: `${p} gave this message no id, so it cannot be deleted.` }
    : line?.deleted ? { kind: "off" as const, label: "Delete message", hint: "Already deleted." } : view("Delete");
  const reasonBody = reason.trim() ? { reason: reason.trim() } : {};

  return (
    <div class="chat-actions">
      <div class="row-buttons">
        {line && <ToolButton view={del} onRun={() => void act("Delete", p, who)} />}
        <span class="timeout">
          <label class="sr-only" for={`to-${target.userId}`}>Timeout length</label>
          <select id={`to-${target.userId}`} value={String(preset)} onChange={(e) => setPreset(Number((e.target as HTMLSelectElement).value))}>
            {(presets.length ? presets : [{ requested: 600, applied: 600, note: "" }]).map((x) => (
              <option key={x.requested} value={String(x.requested)}>{presetLabel(x)}</option>
            ))}
          </select>
          <ToolButton view={view("Timeout")} onRun={() => void act("Timeout", p, { ...who, durationSeconds: preset, ...reasonBody })} />
        </span>
        <ToolButton view={view("Ban")} danger onRun={() => {
          if (confirm(`Ban ${target.user} on ${p}? It is made with the streamer's account and recorded in the mod log.`))
            void act("Ban", p, { ...who, ...reasonBody });
        }} />
        <ToolButton view={view("Unban")} onRun={() => void act("Unban", p, { userId: target.userId, user: target.user })} />
        <ToolButton view={view("Warn")} onRun={() => void act("Warn", p, { ...who, ...reasonBody })} />
        {onCard && <button type="button" class="link" onClick={onCard}>User card</button>}
      </div>
      {chosen?.note && <small class="muted">{chosen.note}</small>}
      <input type="text" class="reason" placeholder="Reason (optional, kept in the mod log)" maxLength={500} value={reason}
        aria-label="Reason" onInput={(e) => setReason((e.target as HTMLInputElement).value)} />
    </div>
  );
}

/** Chat modes and clear chat, per platform, for roles with ChatModes. */
function ModeBar({ platforms, caps, act }: { platforms: string[]; caps: PlatformTools[]; act: Act }) {
  if (platforms.length === 0) return null;
  return (
    <details class="card mode-bar">
      <summary>Chat modes and clear chat</summary>
      {platforms.map((p) => (
        <div key={p} class="mode-row">
          <span class={`chip ${platformClass(p)}`}>{p}</span>
          {[...MODE_ACTIONS, "ShieldMode"].map((action) => {
            const tool = findTool(caps, p, action);
            if (!tool) return null;
            const view = toolView(p, tool);
            if (action === "Clear")
              return <ToolButton key={action} view={view} danger onRun={() => {
                if (confirm(`Clear all of ${p}'s chat for everyone?`)) void act("Clear", p, {});
              }} />;
            if (view.kind !== "run") return <ToolButton key={action} view={view} onRun={() => undefined} />;
            return (
              <span key={action} class="mode-toggle" title={view.hint}>
                {view.label}:
                <button type="button" onClick={() => void act(action, p, { enabled: true })}>On</button>
                <button type="button" onClick={() => void act(action, p, { enabled: false })}>Off</button>
              </span>
            );
          })}
          {caps.find((c) => c.platform === p)?.quota && <QuotaLine quota={caps.find((c) => c.platform === p)!.quota!} />}
        </div>
      ))}
    </details>
  );
}

function QuotaLine({ quota }: { quota: NonNullable<PlatformTools["quota"]> }) {
  return <small class="muted">YouTube quota left today: {quota.remaining.toLocaleString("en")} units ({quota.reserve.toLocaleString("en")} kept for reading chat).</small>;
}

function LogList({ rows }: { rows: LogRow[] }) {
  if (rows.length === 0) return <p class="muted">Nothing in the mod log yet.</p>;
  return (
    <ul class="rows mod-log">
      {rows.map((r) => {
        const detail = logDetail(r);
        return (
          <li key={r.id} class={r.result === "Ok" ? "" : "problem"}>
            <span class={`chip ${platformClass(r.platform)}`}>{r.platform}</span>
            <span><strong>{actorText(r)}</strong> {logSummary(r)}</span>
            <time class="muted">{new Date(r.at).toLocaleString()}</time>
            {detail && <span class="muted log-detail">{detail}</span>}
          </li>
        );
      })}
    </ul>
  );
}

/** The mod log: every mod's actions, newest first, with live rows while shown. */
function ModLog({ platform }: { platform: string }) {
  const { host } = usePortal();
  const [rows, setRows] = useState<LogRow[]>([]);
  const [more, setMore] = useState(false);
  const [problem, setProblem] = useState<LogPage | null>(null);
  const load = async (offset: number) => {
    const a = (await host.request("chat.log", { limit: 50, offset, ...(platform ? { platform } : {}) })
      .catch((e: unknown) => ({ ok: false, error: String(e) }))) as LogPage;
    setProblem(a.ok ? null : a);
    if (!a.ok) return;
    setRows((old) => (offset === 0 ? a.rows ?? [] : [...old, ...(a.rows ?? [])]));
    setMore(a.more === true);
  };
  const pushing = useSubscription("modlog", platform ? { platform } : {}, {
    onEvents: (events) => setRows((old) => events.reduce<LogRow[]>((acc, e) => {
      const row = (e as { row?: LogRow }).row;
      return row ? upsertRow(acc, row) : acc;
    }, old)),
    onResync: () => void load(0),
  });
  useEffect(() => {
    void load(0);
    const poll = pushing ? undefined : setInterval(() => void load(0), 10000);
    return () => poll && clearInterval(poll);
  }, [platform, pushing]);
  return (
    <section class="card">
      <LoadError data={problem} />
      <LogList rows={rows} />
      {more && <button type="button" onClick={() => void load(rows.length)}>Older</button>}
    </section>
  );
}

/** One person: the platform and the stable user id (names can be copied), their recent lines, log rows and the tools. */
function UserCardView({ target, caps, act, onClose }: { target: Target; caps: PlatformTools[]; act: Act; onClose: () => void }) {
  const { host } = usePortal();
  const [data, setData] = useState<UserCard | null>(null);
  const load = () => void host.request("chat.user", { platform: target.platform, userId: target.userId })
    .then((a) => setData(a as UserCard), (e: unknown) => setData({ ok: false, error: String(e) } as UserCard));
  useEffect(load, [target.platform, target.userId]);
  const actAndReload: Act = async (...args) => {
    const ok = await act(...args);
    load();
    return ok;
  };
  return (
    <aside class="card user-card" aria-label="User card">
      <div class="page-head">
        <h2>{data?.name || target.user}</h2>
        <button type="button" class="link" onClick={onClose}>Close</button>
      </div>
      <p class="muted">
        <span class={`chip ${platformClass(target.platform)}`}>{target.platform}</span>{" "}
        User id <code class="mono">{target.userId}</code>
        {data?.roles?.length ? ` · ${data.roles.join(", ")}` : ""}
      </p>
      <p class="muted small">Display names can be copied by anyone; the platform and user id are what identify this account.</p>
      <LoadError data={data} />
      {data?.inForce && (
        <p class="status-pending">
          {data.inForce.action === "ban" ? "Banned" : `Timed out${data.inForce.appliedSeconds ? ` for ${formatDuration(data.inForce.appliedSeconds)}` : ""}`}
          {" "}by {actorText(data.inForce)}.
        </p>
      )}
      <Actions target={target} caps={caps} act={actAndReload} />
      {data?.suspiciousKnown && <SuspiciousTools target={target} flagged={data.suspicious} caps={caps} act={actAndReload} />}
      <h3>Recent messages</h3>
      {data?.messages?.length ? (
        <ul class="rows">
          {data.messages.map((m) => (
            <li key={m.id} class={m.deleted ? "deleted" : ""}><time class="muted">{time(m.time)}</time> <span class="chat-text">{m.deleted ? "(deleted) " : ""}{m.text}</span></li>
          ))}
        </ul>
      ) : <p class="muted">None in Cuebot's recent chat.</p>}
      {data?.log && (<><h3>Mod log</h3><LogList rows={data.log} /></>)}
    </aside>
  );
}

/** Monitor, Restrict and Not suspicious for one chatter (Twitch), with their current status. */
function SuspiciousTools({ target, flagged, caps, act }: { target: Target; flagged?: Suspicious; caps: PlatformTools[]; act: Act }) {
  const p = target.platform;
  const who = { userId: target.userId, user: target.user };
  const view = (action: string) => toolView(p, findTool(caps, p, action));
  return (
    <div class="chat-actions suspicious">
      <p class="muted small">{suspiciousText(flagged) || "Not marked suspicious."}</p>
      <div class="row-buttons">
        <ToolButton view={view("MonitorUser")} onRun={() => void act("MonitorUser", p, who)} />
        <ToolButton view={view("RestrictUser")} onRun={() => void act("RestrictUser", p, who)} />
        <ToolButton view={view("ClearSuspicious")} onRun={() => void act("ClearSuspicious", p, who)} />
      </div>
    </div>
  );
}

/** Messages the platform held for review, waiting first, with Allow and Deny. */
function HeldLane({ waiting, decided, caps, decide, onCard }: {
  waiting: HeldItem[]; decided: HeldItem[]; caps: PlatformTools[]; decide: (h: HeldItem, allow: boolean) => Promise<void>;
  onCard: (h: HeldItem) => void;
}) {
  return (
    <section class="card held-lane" aria-label="Held messages">
      <h2>Held for review <span class={waiting.length ? "chip count" : "chip"}>{waiting.length}</span></h2>
      {waiting.length === 0 && <p class="muted small">Nothing is waiting. Messages AutoMod or a blocked term holds back appear here.</p>}
      <ul class="rows">
        {waiting.map((h) => (
          <li key={`${h.platform}|${h.id}`} class="held">
            <span class={`chip ${platformClass(h.platform)}`}>{h.platform}</span>
            <button type="button" class="link chat-user" title={`${h.platform} user id ${h.userId}`} onClick={() => onCard(h)}>{h.user}</button>
            <span class="chat-text">{h.text}</span>
            {h.reason && <small class="muted">{h.reason}</small>}
            <span class="row-buttons">
              <ToolButton view={toolView(h.platform, findTool(caps, h.platform, "ApproveHeld"), "Allow")} primary onRun={() => void decide(h, true)} />
              <ToolButton view={toolView(h.platform, findTool(caps, h.platform, "DenyHeld"), "Deny")} onRun={() => void decide(h, false)} />
            </span>
          </li>
        ))}
      </ul>
      {decided.length > 0 && (
        <details>
          <summary class="muted small">Recently decided ({decided.length})</summary>
          <ul class="rows">
            {decided.map((h) => (
              <li key={`${h.platform}|${h.id}`} class="muted">
                <span class={`chip ${platformClass(h.platform)}`}>{h.platform}</span> {h.user}: <span class="chat-text">{h.text}</span>
                {" "}<small>{heldStatusText(h)}</small>
              </li>
            ))}
          </ul>
        </details>
      )}
    </section>
  );
}

/** Shield Mode, unban requests and blocked terms, per platform that has them, each for roles with the right. */
function ModTools({ queues, platform, caps, act, reload }: {
  queues: QueueState; platform: string; caps: PlatformTools[]; act: Act; reload: () => void;
}) {
  const { host, can } = usePortal();
  const [lists, setLists] = useState<PlatformQueues[]>([]);
  const [problem, setProblem] = useState<Queues | null>(null);
  const wantUnban = can("ChatBan");
  const wantTerms = can("BlockedTerms");
  const loadLists = async () => {
    if (!wantUnban && !wantTerms) return;
    const a = (await host.request("chat.queues", { unbanRequests: wantUnban, blockedTerms: wantTerms })
      .catch((e: unknown) => ({ ok: false, error: String(e) }))) as Queues;
    setProblem(a.ok ? null : a);
    if (a.ok) setLists(a.platforms ?? []);
  };
  useEffect(() => void loadLists(), [queues.unbanVersion, queues.termsVersion]);
  const actAndReload: Act = async (...args) => {
    const ok = await act(...args);
    if (ok) {
      reload();
      void loadLists();
    }
    return ok;
  };
  const shown = queues.platforms.filter((q) => !platform || q.platform.toLowerCase() === platform.toLowerCase());
  if (shown.length === 0) return <p class="muted">No platform here has these tools. Shield Mode, unban requests and blocked terms are Twitch's.</p>;
  return (
    <div class="mod-tools">
      <LoadError data={problem} />
      {shown.map((q) => {
        const list = lists.find((l) => l.platform.toLowerCase() === q.platform.toLowerCase());
        return (
          <section key={q.platform} class="card">
            <h2><span class={`chip ${platformClass(q.platform)}`}>{q.platform}</span></h2>
            <ShieldRow queues={q} caps={caps} act={actAndReload} />
            {wantUnban && <UnbanRequests platform={q.platform} list={list} caps={caps} act={actAndReload} />}
            {wantTerms && <BlockedTerms platform={q.platform} list={list} caps={caps} act={actAndReload} />}
          </section>
        );
      })}
    </div>
  );
}

function ShieldRow({ queues, caps, act }: { queues: PlatformQueues; caps: PlatformTools[]; act: Act }) {
  const p = queues.platform;
  const view = toolView(p, findTool(caps, p, "ShieldMode"));
  const on = queues.shield?.active === true;
  return (
    <div class="shield-row">
      <h3>Shield Mode</h3>
      <p class={on ? "status-pending" : "muted"}>{shieldText(queues.shield)}</p>
      {view.kind === "run" ? (
        <button type="button" class={on ? "" : "danger"} title={view.hint} onClick={() => {
          if (on || confirm(`Turn on Shield Mode on ${p}? It applies the strict chat settings the streamer chose for raids and harassment.`))
            void act("ShieldMode", p, { enabled: !on });
        }}>{on ? "Turn Shield Mode off" : "Turn Shield Mode on"}</button>
      ) : <ToolButton view={view} onRun={() => undefined} />}
    </div>
  );
}

function UnbanRequests({ platform, list, caps, act }: { platform: string; list?: PlatformQueues; caps: PlatformTools[]; act: Act }) {
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const pending = (list?.unbanRequests ?? []).filter((r) => r.status === "Pending");
  const view = (action: string, label: string) => toolView(platform, findTool(caps, platform, action), label);
  const send = (action: string, id: string) => {
    const text = (answers[id] ?? "").trim();
    void act(action, platform, { itemId: id, ...(text ? { reason: text } : {}) }).then((ok) => {
      if (ok) setAnswers((a) => ({ ...a, [id]: "" }));
    });
  };
  return (
    <div class="unban-requests">
      <h3>Unban requests <span class="chip">{pending.length}</span></h3>
      {list?.unbanRequestsError && <p class="status-problem">{list.unbanRequestsError}</p>}
      {!list ? <p class="muted small">Loading...</p> : pending.length === 0 && <p class="muted small">No open requests.</p>}
      <ul class="rows">
        {pending.map((r) => (
          <li key={r.id}>
            <span><strong>{r.user}</strong> <code class="mono muted" title="User id">{r.userId}</code></span>
            <time class="muted">{new Date(r.createdAt).toLocaleString()}</time>
            <span class="chat-text">{r.text}</span>
            <input type="text" class="reason" maxLength={500} placeholder="Answer to the viewer (optional)" aria-label="Answer to the viewer"
              value={answers[r.id] ?? ""} onInput={(e) => setAnswers((a) => ({ ...a, [r.id]: (e.target as HTMLInputElement).value }))} />
            <span class="row-buttons">
              <ToolButton view={view("ApproveUnbanRequest", "Approve (unban)")} primary onRun={() => {
                if (confirm(`Approve ${r.user}'s request? This lifts the ban on ${platform}.`)) send("ApproveUnbanRequest", r.id);
              }} />
              <ToolButton view={view("DenyUnbanRequest", "Deny")} onRun={() => send("DenyUnbanRequest", r.id)} />
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function BlockedTerms({ platform, list, caps, act }: { platform: string; list?: PlatformQueues; caps: PlatformTools[]; act: Act }) {
  const [term, setTerm] = useState("");
  const [hint, setHint] = useState<string | null>(null);
  const terms = list?.blockedTerms ?? [];
  const addView = toolView(platform, findTool(caps, platform, "AddBlockedTerm"), "Block");
  const removeView = toolView(platform, findTool(caps, platform, "RemoveBlockedTerm"), "Remove");
  const add = () => {
    const problem = termProblem(term);
    setHint(problem);
    if (problem) return;
    void act("AddBlockedTerm", platform, { term: term.trim() }).then((ok) => ok && setTerm(""));
  };
  return (
    <div class="blocked-terms">
      <h3>Blocked terms <span class="chip">{terms.length}</span></h3>
      {list?.blockedTermsError && <p class="status-problem">{list.blockedTermsError}</p>}
      <form class="row-buttons" onSubmit={(e) => { e.preventDefault(); add(); }}>
        <input type="text" maxLength={MAX_TERM} placeholder="Word or phrase" aria-label="Term to block" value={term}
          onInput={(e) => setTerm((e.target as HTMLInputElement).value)} />
        <ToolButton view={addView} primary onRun={add} />
      </form>
      {hint && <small class="status-problem">{hint}</small>}
      {!list ? <p class="muted small">Loading...</p> : terms.length === 0 && <p class="muted small">No blocked terms.</p>}
      <ul class="rows">
        {terms.map((t) => (
          <li key={t.id}>
            <span class="chat-text mono">{t.text}</span>
            <ToolButton view={removeView} onRun={() => {
              if (confirm(`Stop blocking "${t.text}" on ${platform}?`)) void act("RemoveBlockedTerm", platform, { itemId: t.id });
            }} />
          </li>
        ))}
      </ul>
    </div>
  );
}
