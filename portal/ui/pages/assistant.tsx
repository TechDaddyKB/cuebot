// The streamer's Assistant, for mods: a conversation that can only do what the mod's role allows (PortalAssistant.cs).

import { useEffect, useRef, useState } from "preact/hooks";
import { usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";
import { sendFile, type UploadKind, type Uploads } from "./uploads.js";

interface Line {
  id: number;
  seq: number;
  kind: "user" | "assistant" | "tool" | "confirm" | "notice" | "error";
  text: string;
  state: string;
}

interface Status extends Answer {
  limit?: number;
  used?: number;
  busy?: boolean;
  seq?: number;
  lines?: Line[];
}

const TOOL_STATE: Record<string, string> = { running: "…", done: "✓", failed: "✗" };

function kindOf(file: File): UploadKind | null {
  if (file.type.startsWith("audio/")) return "Sound";
  if (file.type.startsWith("video/")) return "Video";
  if (file.type.startsWith("image/")) return "Image";
  return null;
}

export function AssistantPage() {
  const { call, host, notify, can } = usePortal();
  const [status, setStatus] = useState<Status | null>(null);
  const [lines, setLines] = useState<Map<number, Line>>(new Map());
  const [busy, setBusy] = useState(false);
  const [used, setUsed] = useState(0);
  const [text, setText] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [progress, setProgress] = useState<string | null>(null);
  const [uploads, setUploads] = useState<Uploads | null>(null);
  const seq = useRef(0);
  const bottom = useRef<HTMLDivElement>(null);
  const canAttach = can("UploadSounds") || can("UploadMedia");

  const merge = (incoming: Line[] | undefined, newSeq: number | undefined, replace = false) => {
    setLines((old) => {
      const next = replace ? new Map<number, Line>() : new Map(old);
      for (const l of incoming ?? []) next.set(l.id, l);
      return next;
    });
    if (newSeq !== undefined) seq.current = Math.max(replace ? 0 : seq.current, newSeq);
  };

  const load = async () => {
    const answer = (await host.request("assistant.status").catch((e: unknown) => ({ ok: false, error: String(e) }))) as Status;
    setStatus(answer);
    if (!answer.ok) return;
    merge(answer.lines, answer.seq, true);
    setBusy(!!answer.busy);
    setUsed(answer.used ?? 0);
  };

  useEffect(() => {
    void load();
    if (canAttach) void host.request("uploads.list").then((a) => setUploads(a as Uploads)).catch(() => undefined);
  }, []);

  // While a turn runs, ask for new lines every second.
  useEffect(() => {
    if (!busy) return;
    let live = true;
    const timer = setInterval(async () => {
      const answer = (await host.request("assistant.poll", { after: seq.current }).catch(() => null)) as Status | null;
      if (!live || !answer?.ok) return;
      merge(answer.lines, answer.seq);
      if (!answer.busy) setBusy(false);
    }, 1000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [busy]);

  const sorted = [...lines.values()].sort((a, b) => a.id - b.id);
  useEffect(() => bottom.current?.scrollIntoView({ block: "end" }), [sorted.length, sorted[sorted.length - 1]?.text]);

  const send = async (e: Event) => {
    e.preventDefault();
    if (busy || progress !== null || (!text.trim() && files.length === 0)) return;
    const attachments: string[] = [];
    try {
      for (const file of files) {
        const kind = kindOf(file);
        const limit = kind ? uploads?.limits?.[kind] : undefined;
        if (!kind || !limit || !uploads?.canUpload?.includes(kind)) {
          notify(`"${file.name}" is not a kind of file you can upload.`, "error");
          return;
        }
        const sent = await sendFile(call, notify, file, kind, limit, setProgress);
        if (!sent) return;
        attachments.push(sent.id);
      }
    } finally {
      setProgress(null);
    }
    const words = text.trim() || "I attached a file.";
    const answer = await call("assistant.send", { text: words, attachments });
    if (!answer.ok) return;
    setText("");
    setFiles([]);
    setUsed((answer.used as number | undefined) ?? used + 1);
    // Show the message at once; the next poll brings the real line.
    setBusy(true);
  };

  const answerConfirm = async (line: Line, approve: boolean) => {
    if ((await call("assistant.answer", { line: line.id, approve })).ok) merge([{ ...line, state: approve ? "approved" : "denied" }], undefined);
  };

  const reset = async () => {
    if (!confirm("Start a new conversation? The assistant forgets this one.")) return;
    await call("assistant.reset");
    await load();
  };

  if (status && !status.ok) {
    return (
      <>
        <h1>Assistant</h1>
        <LoadError data={status} />
      </>
    );
  }

  const left = Math.max(0, (status?.limit ?? 0) - used);
  return (
    <>
      <h1>Assistant</h1>
      <p class="muted">Ask the streamer's assistant to set things up for you, for example “make a channel point redeem called Bonk that plays
        this sound”. It can only do what your role allows, and your changes follow your role's rules (live, or waiting for the streamer).</p>
      <section class="card assistant">
        <div class="assistant-lines" aria-live="polite">
          {sorted.length === 0 && <p class="muted">No messages yet.</p>}
          {sorted.map((l) => {
            switch (l.kind) {
              case "user": return <div key={l.id} class="msg user">{l.text}</div>;
              case "assistant": return <div key={l.id} class="msg ai">{l.text}</div>;
              case "tool": return <div key={l.id} class={`msg tool ${l.state}`}>{TOOL_STATE[l.state] ?? ""} {l.text}</div>;
              case "confirm":
                return (
                  <div key={l.id} class="msg confirm">
                    <span>{l.text}?</span>
                    {l.state === "waiting"
                      ? <span class="row-buttons">
                          <button type="button" class="primary" onClick={() => void answerConfirm(l, true)}>Approve</button>
                          <button type="button" onClick={() => void answerConfirm(l, false)}>Deny</button>
                        </span>
                      : <span class="muted">{l.state === "approved" ? "Approved" : "Denied"}</span>}
                  </div>
                );
              case "error": return <div key={l.id} class="msg error" role="alert">{l.text}</div>;
              default: return <div key={l.id} class="msg info muted">{l.text}</div>;
            }
          })}
          {busy && <div class="msg info muted">Working…</div>}
          <div ref={bottom} />
        </div>
        <form class="assistant-compose" onSubmit={send}>
          <textarea rows={3} aria-label="Message" placeholder="What should I set up?" value={text} disabled={busy}
            onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) void send(e);
            }} />
          {files.length > 0 && (
            <ul class="attached">
              {files.map((f, i) => (
                <li key={`${f.name}-${i}`}>{f.name} <button type="button" class="link" aria-label={`Remove ${f.name}`}
                  onClick={() => setFiles(files.filter((_, j) => j !== i))}>×</button></li>
              ))}
            </ul>
          )}
          <div class="form-row">
            <span class="muted" role="status">{progress ?? `${left} of ${status?.limit ?? 0} messages left today`}</span>
            <span class="row-buttons">
              {canAttach && uploads?.ok && uploads.available && (
                <label class="button">
                  Attach a file
                  <input type="file" hidden multiple disabled={busy || progress !== null}
                    accept={(uploads.canUpload ?? []).map((k) => uploads.accept?.[k] ?? "").filter(Boolean).join(",")}
                    onChange={(e) => {
                      const input = e.target as HTMLInputElement;
                      setFiles([...files, ...Array.from(input.files ?? [])].slice(0, 5));
                      input.value = "";
                    }} />
                </label>
              )}
              {busy
                ? <button type="button" onClick={() => void call("assistant.stop")}>Stop</button>
                : <button type="button" onClick={() => void reset()} disabled={sorted.length === 0}>New conversation</button>}
              <button type="submit" class="primary" disabled={busy || progress !== null || left === 0}>Send</button>
            </span>
          </div>
        </form>
      </section>
    </>
  );
}
