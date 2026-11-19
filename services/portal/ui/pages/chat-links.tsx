// Identity links, notes and linked bans and timeouts on the Chat page (docs/design/UNIFIED_MOD_CHAT_PLAN.md, phases 4 and 5).
// Shown only while the streamer has Identity links turned on. Everything is rendered as text. Cuebot reads accounts,
// names and evidence from its own chat and checks every request again; a preview is kept by Cuebot and this page only
// says which rows are ticked.

import { useEffect, useState } from "preact/hooks";
import { usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";
import {
  MAX_EVIDENCE, MAX_LINK_NOTE, MAX_NOTE, NOTE_WARNING, accountButtons, actorText, byHandRows, countdown, executeProblem,
  formatDuration, logSummary, needsSecondConfirmation, pickable, platformClass, quotaTotal, resultText, safeUrl,
  secondConfirmationText, selectedRows, statusLabel, toggleRow, type Candidate, type ChatLine, type LaterArrival,
  type LinkedAccount, type LinkedPreview, type LinksCaps, type LogRow, type ModNote, type PersonCard, type RowResult,
} from "./chat-model.js";

interface PersonAnswer extends Answer, Partial<PersonCard> {}
interface UndoAnswer extends Answer { summary?: string; rows?: { platform: string; name: string; result: string; message: string; websiteUrl?: string }[]; cannotUndo?: string[] }

const statusClass = (s: string) => (s === "Verified" || s === "Confirmed" ? "status-ok" : s === "Suggested" ? "status-pending" : "muted");
const date = (iso: string) => new Date(iso).toLocaleDateString();

/** Text that a website link can be built from, or nothing. */
function SiteLink({ url, label }: { url?: string; label: string }) {
  const safe = safeUrl(url);
  return safe ? <a class="button" href={safe} target="_blank" rel="noopener noreferrer">{label}</a> : null;
}

// ---------------------------------------------------------------- user card section

/** The user card's linked accounts, Same person as..., !link verification, evidence and notes. */
export function LinkedSection({ platform, userId, messages, version }: { platform: string; userId: string; messages: ChatLine[]; version: number }) {
  const { host } = usePortal();
  const [data, setData] = useState<PersonAnswer | null>(null);
  const load = () => void host.request("chat.person", { platform, userId })
    .then((a) => setData(a as PersonAnswer), (e: unknown) => setData({ ok: false, error: String(e) }));
  useEffect(load, [platform, userId, version]);
  if (!data) return <p class="muted small">Loading linked accounts...</p>;
  if (!data.ok) return <LoadError data={data} />;
  const card = data as PersonCard;
  return (
    <section class="linked" aria-label="Linked accounts">
      <h3>Linked accounts</h3>
      <AccountList card={card} reload={load} />
      {card.hints.length > 0 && (
        <p class="muted small">Same name elsewhere (only a hint, not a link): {card.hints.map((h) => `${h.platform}: ${h.name}`).join(", ")}</p>
      )}
      {card.rights.propose && <SamePerson card={card} messages={messages} reload={load} />}
      {card.rights.propose && <Verify platform={platform} userId={userId} name={card.name} />}
      {card.person && card.person.evidence.length > 0 && (
        <details>
          <summary class="muted small">Evidence ({card.person.evidence.length})</summary>
          <ul class="rows">
            {card.person.evidence.map((e, i) => (
              <li key={i}><span class="chip">{e.kind}</span> <span class="chat-text">{e.quote}</span> <small class="muted">{e.addedBy}, {date(e.at)}</small></li>
            ))}
          </ul>
        </details>
      )}
      {card.rights.notesRead && <Notes card={card} reload={load} />}
    </section>
  );
}

function AccountList({ card, reload }: { card: PersonCard; reload: () => void }) {
  const { call, notify } = usePortal();
  const [unlinking, setUnlinking] = useState<{ account: LinkedAccount; wrong: boolean; dissolve: boolean } | null>(null);
  const accounts = card.person?.accounts ?? [];
  if (accounts.length === 0) return <p class="muted small">Not linked to any other account.</p>;
  const run = async (op: string, body: Record<string, unknown>) => {
    const a = await call(op, body);
    if (a.ok) notify(String(a.message ?? "Done."));
    reload();
  };
  return (
    <>
      <ul class="rows linked-accounts">
        {accounts.map((a) => {
          const b = accountButtons(a, card.rights);
          return (
            <li key={a.linkId}>
              <span class={`chip ${platformClass(a.platform)}`}>{a.platform}</span>
              <span><strong>{a.name}</strong>{a.isThis ? " (this account)" : ""} <code class="mono muted" title="User id">{a.userId}</code></span>
              <span class={statusClass(a.between)} title={statusLabel(a.status)}>{a.isThis ? statusLabel(a.status) : a.linkText}</span>
              <span class="row-buttons">
                {b.confirm && <button type="button" class="primary" onClick={() => void run("chat.link.confirm", { linkId: a.linkId })}>Confirm</button>}
                {!b.confirm && b.confirmHint && <small class="muted">{b.confirmHint}</small>}
                {b.wrong && <button type="button" onClick={() => {
                  if (confirm(`Mark the link of ${a.name} on ${a.platform} as wrong? It will not be suggested again.`))
                    void run("chat.link.reject", { linkId: a.linkId });
                }}>Wrong</button>}
                {b.unlink && a.status !== "Suggested" && (
                  <button type="button" onClick={() => setUnlinking({ account: a, wrong: false, dissolve: a.isThis })}>Unlink</button>
                )}
              </span>
            </li>
          );
        })}
      </ul>
      {unlinking && <UnlinkDialog {...unlinking} onClose={() => { setUnlinking(null); reload(); }} />}
    </>
  );
}

/** Unlink, with the actions applied through the link that are still in force (ticked when the link was wrong). */
function UnlinkDialog({ account, wrong: initialWrong, dissolve, onClose }: { account: LinkedAccount; wrong: boolean; dissolve: boolean; onClose: () => void }) {
  const { host, call, notify } = usePortal();
  const [wrong, setWrong] = useState(initialWrong);
  const [preview, setPreview] = useState<(Answer & { inForce?: LogRow[]; question?: string; canReverse?: boolean; reverseByDefault?: boolean }) | null>(null);
  const [reverse, setReverse] = useState<Set<string>>(new Set());
  const [reason, setReason] = useState("");
  useEffect(() => {
    void host.request("chat.link.unlinkPreview", { linkId: account.linkId, dissolve, wrong })
      .then((a) => {
        const p = a as typeof preview & Answer;
        setPreview(p);
        setReverse(new Set(p?.reverseByDefault && p.canReverse ? (p.inForce ?? []).map((r) => r.id) : []));
      }, (e: unknown) => setPreview({ ok: false, error: String(e) }));
  }, [account.linkId, wrong]);
  const send = async () => {
    const a = await call("chat.link.unlink", { linkId: account.linkId, dissolve, wrong, reason: reason.trim(), reverse: [...reverse] });
    if (!a.ok) return;
    const reversed = (a as UndoAnswer & { reversed?: UndoAnswer }).reversed;
    notify(`${String(a.message)}${reversed?.summary ? ` ${reversed.summary}` : ""}`);
    onClose();
  };
  return (
    <div class="card dialog" role="dialog" aria-label="Unlink">
      <h3>{dissolve ? "Unlink all these accounts" : `Unlink ${account.name} on ${account.platform}`}</h3>
      <label><input type="checkbox" checked={wrong} onChange={() => setWrong(!wrong)} /> The link was wrong (do not suggest it again)</label>
      <LoadError data={preview} />
      {preview?.ok && (preview.inForce?.length ?? 0) > 0 && (
        <>
          <p>{preview.question}</p>
          {!preview.canReverse && <p class="muted small">Your role does not include lifting bans; ask someone who can.</p>}
          <ul class="rows">
            {preview.inForce!.map((r) => (
              <li key={r.id}>
                <label>
                  <input type="checkbox" disabled={!preview.canReverse} checked={reverse.has(r.id)} onChange={() => {
                    const next = new Set(reverse);
                    if (next.has(r.id)) next.delete(r.id); else next.add(r.id);
                    setReverse(next);
                  }} />
                  {" "}<span class={`chip ${platformClass(r.platform)}`}>{r.platform}</span> {actorText(r)} {logSummary(r)}
                </label>
              </li>
            ))}
          </ul>
        </>
      )}
      <input type="text" class="reason" maxLength={500} placeholder="Reason (optional, kept in the mod log)" aria-label="Reason" value={reason}
        onInput={(e) => setReason((e.target as HTMLInputElement).value)} />
      <div class="row-buttons">
        <button type="button" class="danger" disabled={!preview?.ok} onClick={() => void send()}>Unlink</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </div>
  );
}

/** "Same person as...": pick the other account, attach the messages that show it, add a note. */
function SamePerson({ card, messages, reload }: { card: PersonCard; messages: ChatLine[]; reload: () => void }) {
  const { call, notify } = usePortal();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [picked, setPicked] = useState<Candidate | null>(null);
  const [attached, setAttached] = useState<Set<string>>(new Set());
  const [note, setNote] = useState("");
  const [conflict, setConflict] = useState<LinkedAccount[] | null>(null);
  if (!open) return <button type="button" onClick={() => setOpen(true)}>Same person as...</button>;
  const list = pickable(card.hints, card.candidates ?? [], query).slice(0, 30);
  const evidence = [
    ...messages.map((m) => ({ id: m.id, who: `${m.platform}, ${m.user}`, text: m.text })),
    ...(picked?.messages ?? []).map((m) => ({ id: m.id, who: `${picked!.platform}, ${picked!.name}`, text: m.text })),
  ];
  const send = async (choice?: "Merge" | "Move") => {
    if (!picked) return;
    const a = await call("chat.link.propose", {
      platform: card.platform, userId: card.userId, otherPlatform: picked.platform, otherUserId: picked.userId,
      note: note.trim(), messageIds: [...attached], ...(choice ? { choice } : {}),
    });
    if (a.needsChoice) {
      setConflict((a.conflict as LinkedAccount[]) ?? []);
      return;
    }
    if (!a.ok) return;
    notify(String(a.message));
    setOpen(false);
    setPicked(null);
    setConflict(null);
    reload();
  };
  return (
    <div class="card same-person">
      <h4>Same person as...</h4>
      <p class="muted small">A suggestion until someone else confirms it. Attach the messages that show it (for example the viewer saying so).</p>
      <input type="search" placeholder="Name or user id" aria-label="Find the other account" value={query}
        onInput={(e) => setQuery((e.target as HTMLInputElement).value)} />
      <ul class="rows pick-list">
        {list.length === 0 && <li class="muted small">Nobody matches in the recent chat.</li>}
        {list.map((c) => (
          <li key={`${c.platform}|${c.userId}`}>
            <label>
              <input type="radio" name="same-person" checked={picked?.platform === c.platform && picked.userId === c.userId}
                onChange={() => { setPicked(c); setConflict(null); }} />
              {" "}<span class={`chip ${platformClass(c.platform)}`}>{c.platform}</span> {c.name} <code class="mono muted">{c.userId}</code>
            </label>
          </li>
        ))}
      </ul>
      {evidence.length > 0 && (
        <details>
          <summary class="small">Attach messages as evidence ({attached.size} of at most {MAX_EVIDENCE})</summary>
          <ul class="rows">
            {evidence.map((m) => (
              <li key={m.id}>
                <label>
                  <input type="checkbox" checked={attached.has(m.id)} disabled={!attached.has(m.id) && attached.size >= MAX_EVIDENCE} onChange={() => {
                    const next = new Set(attached);
                    if (next.has(m.id)) next.delete(m.id); else next.add(m.id);
                    setAttached(next);
                  }} />
                  {" "}<small class="muted">{m.who}:</small> <span class="chat-text">{m.text}</span>
                </label>
              </li>
            ))}
          </ul>
        </details>
      )}
      <textarea maxLength={MAX_LINK_NOTE} placeholder="Note: why you think so" aria-label="Note" value={note}
        onInput={(e) => setNote((e.target as HTMLTextAreaElement).value)} />
      {conflict && (
        <div class="status-pending">
          <p>{picked?.name} already belongs to another person:</p>
          <ul class="rows">{conflict.map((a) => <li key={a.linkId}>{a.platform}: {a.name} ({a.linkText})</li>)}</ul>
          <div class="row-buttons">
            <button type="button" onClick={() => void send("Merge")}>Merge the two people</button>
            <button type="button" onClick={() => void send("Move")}>Move only this account</button>
          </div>
        </div>
      )}
      <div class="row-buttons">
        <button type="button" class="primary" disabled={!picked || !!conflict} onClick={() => void send()}>Suggest link</button>
        <button type="button" onClick={() => setOpen(false)}>Cancel</button>
      </div>
    </div>
  );
}

/** "Verify with !link": a one-time code for this mod to send the viewer privately, with a countdown. */
function Verify({ platform, userId, name }: { platform: string; userId: string; name: string }) {
  const { call } = usePortal();
  const [code, setCode] = useState<{ code: string; expiresAt: string; instructions: string } | null>(null);
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!code) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [code]);
  const start = async () => {
    const a = await call("chat.link.verify", { platform, userId });
    if (a.ok) setCode({ code: String(a.code), expiresAt: String(a.expiresAt), instructions: String(a.instructions) });
  };
  const left = code ? countdown(code.expiresAt, now) : "";
  return (
    <div class="verify">
      <button type="button" title={`${name} proves both accounts by typing !link and the code from the other one`} onClick={() => void start()}>
        {code ? "New code" : "Verify with !link"}
      </button>
      {code && (
        <div class="card">
          <p><strong class="mono verify-code">{code.code}</strong> <span class={left === "expired" ? "status-problem" : "muted"}>{left === "expired" ? "Expired" : `${left} left`}</span></p>
          <p class="small">{code.instructions}</p>
        </div>
      )}
    </div>
  );
}

function Notes({ card, reload }: { card: PersonCard; reload: () => void }) {
  const { call, notify } = usePortal();
  const [text, setText] = useState("");
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null);
  const notes = card.notes ?? [];
  const run = async (op: string, body: Record<string, unknown>) => {
    const a = await call(op, body);
    if (a.ok) notify(String(a.message ?? "Saved."));
    reload();
    return a.ok;
  };
  const canChange = (n: ModNote) => n.mine || card.rights.notesDelete;
  return (
    <div class="notes">
      <h4>Notes</h4>
      <p class="muted small">{NOTE_WARNING} Notes follow linked accounts.</p>
      {notes.length === 0 && <p class="muted small">No notes.</p>}
      <ul class="rows">
        {notes.map((n) => (
          <li key={n.id}>
            {editing?.id === n.id ? (
              <>
                <textarea maxLength={MAX_NOTE} aria-label="Note" value={editing.text} onInput={(e) => setEditing({ id: n.id, text: (e.target as HTMLTextAreaElement).value })} />
                <span class="row-buttons">
                  <button type="button" class="primary" onClick={() => void run("chat.note.edit", { noteId: n.id, text: editing.text }).then((ok) => ok && setEditing(null))}>Save</button>
                  <button type="button" onClick={() => setEditing(null)}>Cancel</button>
                </span>
              </>
            ) : (
              <>
                <span class="chat-text">{n.text}</span>
                <small class="muted">{n.author}, {date(n.editedAt)}{n.platform !== card.platform || n.userId !== card.userId ? ` (on ${n.platform})` : ""}</small>
                {canChange(n) && (
                  <span class="row-buttons">
                    <button type="button" class="link" onClick={() => setEditing({ id: n.id, text: n.text })}>Edit</button>
                    <button type="button" class="link" onClick={() => { if (confirm("Delete this note?")) void run("chat.note.delete", { noteId: n.id }); }}>Delete</button>
                  </span>
                )}
              </>
            )}
          </li>
        ))}
      </ul>
      {card.rights.notesAdd && (
        <form onSubmit={(e) => { e.preventDefault(); if (text.trim()) void run("chat.note.add", { platform: card.platform, userId: card.userId, text }).then((ok) => ok && setText("")); }}>
          <textarea maxLength={MAX_NOTE} placeholder="Add a note" aria-label="New note" value={text} onInput={(e) => setText((e.target as HTMLTextAreaElement).value)} />
          <button type="submit" disabled={!text.trim()}>Add note</button>
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- linked ban and timeout

export interface LinkedRequest { action: "Ban" | "Timeout"; platform: string; userId: string; user: string; messageId?: string; durationSeconds?: number }

/**
 * Asks Cuebot for the preview. Not linked: null (the page uses the plain tools). Linked: the preview for the dialog.
 */
export async function linkedPreview(call: (op: string, b?: Record<string, unknown>) => Promise<Answer>, notify: (t: string) => void,
  r: LinkedRequest): Promise<LinkedPreview | null | "failed"> {
  const a = await call("chat.linked.preview", { ...r });
  if (!a.ok) return "failed";
  const p = a as unknown as LinkedPreview;
  if (p.linkedNote) notify(p.linkedNote);
  return p.linked ? p : null;
}

/** The linked dialog: preview table, required reason, second confirmation, per-row results with Retry, I did it and Undo. */
export function LinkedDialog({ preview, links, onClose }: { preview: LinkedPreview; links: LinksCaps; onClose: () => void }) {
  const { call, notify } = usePortal();
  const [ticked, setTicked] = useState<Set<string>>(new Set(preview.defaultTicked));
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<{ incidentId: string; summary: string; rows: RowResult[] } | null>(null);
  const [undo, setUndo] = useState<UndoAnswer | null>(null);
  const problem = executeProblem(preview, ticked, reason);
  const quota = quotaTotal(preview, ticked);
  const verb = preview.action === "Ban" ? "Ban" : `Timeout${preview.requestedSeconds ? ` ${formatDuration(preview.requestedSeconds)}` : ""}`;

  const execute = async () => {
    if (problem) return;
    let confirmed = false;
    if (needsSecondConfirmation(preview, ticked)) {
      if (!confirm(`${secondConfirmationText(preview, ticked)} Each ban is made with the streamer's accounts and recorded in the mod log.`)) return;
      confirmed = true;
    }
    setBusy(true);
    let a = await call("chat.linked.execute", { previewId: preview.previewId, ticked: [...ticked], reason: reason.trim(), confirmed });
    if (!a.ok && a.needsConfirmation && confirm(String(a.confirmText))) {
      a = await call("chat.linked.execute", { previewId: preview.previewId, ticked: [...ticked], reason: reason.trim(), confirmed: true });
    }
    setBusy(false);
    if (!a.ok) return;
    notify(String(a.summary));
    setDone({ incidentId: String(a.incidentId), summary: String(a.summary), rows: (a.rows as RowResult[]) ?? [] });
  };
  const updateRow = (r: RowResult) => setDone((d) => d && { ...d, rows: d.rows.map((x) => (x.rowId === r.rowId ? { ...x, ...r } : x)) });
  const retry = async (r: RowResult) => {
    const a = await call("chat.linked.retry", { incidentId: done!.incidentId, rowId: r.rowId });
    updateRow({ ...r, ...(a as unknown as RowResult), canRetry: a.ok ? false : r.canRetry });
  };
  const manual = async (r: RowResult) => {
    const a = await call("chat.linked.manual", { incidentId: done!.incidentId, rowId: r.rowId });
    if (a.ok) updateRow({ ...r, result: "Manual", awaitingManual: false });
  };
  const undoIncident = async () => {
    if (!confirm("Lift every ban and timeout of this action that is still in force?")) return;
    const a = await call("chat.incident.undo", { incidentId: done!.incidentId });
    if (a.ok) {
      setUndo(a as UndoAnswer);
      notify(String(a.summary));
    }
  };

  return (
    <div class="card dialog linked-dialog" role="dialog" aria-label={`${verb} linked accounts`}>
      <div class="page-head">
        <h3>{verb}: {preview.name} and linked accounts</h3>
        <button type="button" class="link" onClick={onClose}>Close</button>
      </div>
      {!done ? (
        <>
          <p class="muted small">Nothing has been sent yet. Unticked rows are left alone; suggestions start unticked.</p>
          <table class="linked-preview">
            <thead><tr><th></th><th>Account</th><th>Link</th><th>What will happen</th><th>Notes</th></tr></thead>
            <tbody>
              {preview.rows.map((r) => (
                <tr key={r.rowId} class={r.canTick ? "" : "muted"}>
                  <td>
                    {r.byHand ? "-" : (
                      <input type="checkbox" aria-label={`Include ${r.platform}: ${r.name}`} disabled={!r.canTick} checked={ticked.has(r.rowId)}
                        onChange={() => setTicked(toggleRow(preview, ticked, r.rowId))} />
                    )}
                  </td>
                  <td><span class={`chip ${platformClass(r.platform)}`}>{r.platform}</span> {r.name}</td>
                  <td>{r.linkText}</td>
                  <td>{r.willHappen}{r.byHand && <> <SiteLink url={r.websiteUrl} label={`Open ${r.platform}`} /></>}</td>
                  <td><small>{r.notes}</small></td>
                </tr>
              ))}
            </tbody>
          </table>
          {quota > 0 && <p class="muted small">Uses {quota.toLocaleString("en")} units of YouTube's daily quota.</p>}
          {byHandRows(preview).length > 0 && <p class="muted small">Rows done by hand come back with the website and an "I did it" button.</p>}
          <input type="text" class="reason" required maxLength={preview.maxReason} placeholder="Reason (required, kept in the mod log)" aria-label="Reason"
            value={reason} onInput={(e) => setReason((e.target as HTMLInputElement).value)} />
          {problem && reason.trim() !== "" && <small class="status-problem">{problem}</small>}
          <div class="row-buttons">
            <button type="button" class="danger" disabled={!!problem || busy}
              title={problem ?? ""} onClick={() => void execute()}>{verb} {selectedRows(preview, ticked).length} account{selectedRows(preview, ticked).length === 1 ? "" : "s"}</button>
            <button type="button" onClick={onClose}>Cancel</button>
          </div>
        </>
      ) : (
        <>
          <p>{done.summary}</p>
          <ul class="rows">
            {done.rows.map((r) => (
              <li key={r.rowId} class={r.result === "Failed" ? "problem" : ""}>
                <span class={`chip ${platformClass(r.platform)}`}>{r.platform}</span> {r.name}: <span>{resultText(r)}</span>
                <span class="row-buttons">
                  {r.canRetry && <button type="button" onClick={() => void retry(r)}>Retry</button>}
                  {r.awaitingManual && <><SiteLink url={r.websiteUrl} label={`Open ${r.platform}`} /><button type="button" onClick={() => void manual(r)}>I did it</button></>}
                </span>
              </li>
            ))}
          </ul>
          {links.rights?.ban && done.rows.some((r) => r.result === "Ok" || r.result === "Manual") && !undo && (
            <button type="button" onClick={() => void undoIncident()}>Undo incident</button>
          )}
          {undo && <UndoResult answer={undo} />}
        </>
      )}
    </div>
  );
}

export function UndoResult({ answer }: { answer: UndoAnswer }) {
  return (
    <div class="undo-result">
      <p>{answer.summary}</p>
      <ul class="rows">
        {(answer.rows ?? []).map((r, i) => (
          <li key={i}><span class={`chip ${platformClass(r.platform)}`}>{r.platform}</span> {r.name}: {r.message} <SiteLink url={r.websiteUrl} label={`Open ${r.platform}`} /></li>
        ))}
      </ul>
    </div>
  );
}

/** "Undo incident" in the mod log. */
export function UndoIncidentButton({ incidentId }: { incidentId: string }) {
  const { call, notify } = usePortal();
  const [answer, setAnswer] = useState<UndoAnswer | null>(null);
  if (answer) return <UndoResult answer={answer} />;
  return (
    <button type="button" class="link" onClick={() => {
      if (!confirm("Lift every ban and timeout of this incident that is still in force? Deleted messages cannot be restored.")) return;
      void call("chat.incident.undo", { incidentId }).then((a) => {
        if (a.ok) {
          setAnswer(a as UndoAnswer);
          notify(String(a.summary));
        }
      });
    }}>Undo incident</button>
  );
}

// ---------------------------------------------------------------- later arrivals

/** "This person is banned on Twitch and Kick. Apply here too?" for each newly linked account. */
export function LaterArrivalsStrip({ items, links, onChanged }: { items: LaterArrival[]; links: LinksCaps; onChanged: (items: LaterArrival[]) => void }) {
  const { call, notify } = usePortal();
  const [reasons, setReasons] = useState<Record<string, string>>({});
  if (!links.enabled || !links.rights?.view || items.length === 0) return null;
  const canApply = links.rights.ban && links.rights.linkedBans;
  const send = async (mode: "apply" | "dismiss", item: LaterArrival) => {
    const a = await call("chat.laterArrivals", { mode, id: item.id, ...(mode === "apply" ? { reason: (reasons[item.id] ?? "").trim() } : {}) });
    if (a.ok) notify(String(a.message ?? "Done."));
    if (Array.isArray(a.items)) onChanged(a.items as LaterArrival[]);
  };
  return (
    <section class="card later-arrivals" aria-label="Banned elsewhere">
      <h2>Banned elsewhere <span class="chip count">{items.length}</span></h2>
      <ul class="rows">
        {items.map((item) => (
          <li key={item.id}>
            <span class={`chip ${platformClass(item.platform)}`}>{item.platform}</span>
            <span class="chat-text">{item.text}</span>
            {canApply && (
              <input type="text" class="reason" maxLength={500} placeholder="Reason (required)" aria-label="Reason" value={reasons[item.id] ?? ""}
                onInput={(e) => setReasons((r) => ({ ...r, [item.id]: (e.target as HTMLInputElement).value }))} />
            )}
            <span class="row-buttons">
              {canApply && <button type="button" class="danger" disabled={!(reasons[item.id] ?? "").trim()} onClick={() => {
                if (confirm(`Ban ${item.name} on ${item.platform} too?`)) void send("apply", item);
              }}>Ban here too</button>}
              {links.rights?.ban && <button type="button" onClick={() => void send("dismiss", item)}>Dismiss</button>}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
