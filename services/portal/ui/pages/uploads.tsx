import { useState } from "preact/hooks";
import { useLoad, usePortal, type Answer, type Portal } from "../api.js";
import { LoadError, Select } from "../widgets.js";

export type UploadKind = "Sound" | "Image" | "Video";

export interface UploadItem {
  id: string;
  name: string;
  kind: UploadKind;
  bytes: number;
  durationMs: number;
  width: number;
  height: number;
  animated: boolean;
  by: string;
  at: string;
  canDelete: boolean;
}

export interface Uploads extends Answer {
  available?: boolean;
  canUpload?: UploadKind[];
  accept?: Partial<Record<UploadKind, string>>;
  limits?: Record<UploadKind, { maxBytes: number; maxSeconds: number }>;
  chunkSize?: number;
  items?: UploadItem[];
}

const KIND_NAMES: Record<UploadKind, string> = { Sound: "Sounds", Image: "Pictures and GIFs", Video: "Videos" };
const ONE: Record<UploadKind, string> = { Sound: "a sound", Image: "a picture or GIF", Video: "a video" };
const PARALLEL = 4;

export function describe(i: UploadItem): string {
  const parts: string[] = [];
  if (i.kind !== "Image" || i.animated) parts.push(`${(i.durationMs / 1000).toFixed(1)} s`);
  if (i.kind !== "Sound") parts.push(`${i.width}×${i.height}`);
  parts.push(`${Math.max(1, Math.round(i.bytes / 1024))} KB`);
  return parts.join(" · ");
}

function base64(bytes: Uint8Array): string {
  let text = "";
  for (let i = 0; i < bytes.length; i += 8192) text += String.fromCharCode(...bytes.subarray(i, i + 8192));
  return btoa(text);
}

/** Where an upload ended: its id and whether it is live or waits for the streamer; null when it failed. */
export interface Sent { id: string; state: "live" | "pending" }

/**
 * Sends one file in chunks, then waits while Cuebot checks and cleans it. Progress goes to `progress`; problems are shown
 * with `notify`.
 */
export async function sendFile(call: Portal["call"], notify: Portal["notify"], file: File, kind: UploadKind,
  limit: { maxBytes: number }, progress: (text: string) => void): Promise<Sent | null> {
  if (file.size > limit.maxBytes) {
    notify(`Files like this can be up to ${Math.round(limit.maxBytes / 1048576)} MB.`, "error");
    return null;
  }
  progress("Reading the file…");
  const bytes = new Uint8Array(await file.arrayBuffer());
  const sha256 = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map((b) => b.toString(16).padStart(2, "0")).join("");
  const start = await call("uploads.start", { kind, name: file.name, size: bytes.length, sha256 });
  if (!start.ok) return null;
  const id = start.id as string;
  const size = start.chunkSize as number;
  const count = Math.ceil(bytes.length / size);
  let next = 0, sent = 0, failed = false;
  const worker = async () => {
    while (!failed && next < count) {
      const index = next++;
      const answer = await call("uploads.chunk", { id, index, data: base64(bytes.subarray(index * size, (index + 1) * size)) });
      if (!answer.ok) failed = true;
      progress(`Sending ${Math.round((++sent / count) * 100)}%`);
    }
  };
  await Promise.all(Array.from({ length: Math.min(PARALLEL, count) }, worker));
  if (failed) {
    void call("uploads.cancel", { id });
    return null;
  }
  progress("Checking and cleaning the file…");
  let status = await call("uploads.finish", { id });
  while (status.ok && status.state === "cleaning") {
    await new Promise((r) => setTimeout(r, 1000));
    status = await call("uploads.status", { id });
  }
  if (status.state === "live") {
    notify(`"${file.name}" is uploaded and ready.`);
    return status.itemId ? { id: status.itemId as string, state: "live" } : null;
  }
  if (status.state === "pending") {
    notify(`"${file.name}" is uploaded. The streamer approves it before it can be used.`);
    return status.itemId ? { id: status.itemId as string, state: "pending" } : null;
  }
  if (status.ok && status.error) notify(status.error as string, "error");
  return null;
}

export function UploadsPage() {
  const { call, notify } = usePortal();
  const [data, reload] = useLoad<Uploads>("uploads.list", 10000);
  const kinds = data?.canUpload ?? [];
  const [kind, setKind] = useState<UploadKind | "">("");
  const [file, setFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const chosen = (kind || kinds[0] || "") as UploadKind | "";

  const upload = async (e: Event) => {
    e.preventDefault();
    if (!file || !chosen || !data?.limits) return;
    try {
      const done = await sendFile(call, notify, file, chosen, data.limits[chosen], setProgress);
      if (done) {
        setFile(null);
        (document.getElementById("upload-file") as HTMLInputElement | null)?.form?.reset();
      }
    } finally {
      setProgress(null);
      reload();
    }
  };

  const remove = async (i: UploadItem) => {
    if (!confirm(`Remove "${i.name}"? Steps that use it will have nothing to play or show.`)) return;
    const answer = await call("uploads.delete", { id: i.id });
    if (answer.ok) notify(answer.message as string);
    reload();
  };

  return (
    <>
      <h1>Uploads</h1>
      <LoadError data={data} />
      <p class="muted">Sounds, pictures and videos for the streamer's alerts and actions. Cuebot checks every file and makes a fresh copy of
        it, so only the sound or picture itself reaches the streamer's computer.</p>
      {data?.ok && data.available === false && <p class="load-error">The streamer's Cuebot cannot check files right now, so uploads are off.</p>}
      {data?.ok && data.available && kinds.length > 0 && (
        <form class="card form" onSubmit={upload}>
          <h2>Upload</h2>
          {kinds.length > 1 && <Select label="What is it?" value={chosen} options={kinds.map((k) => [k, ONE[k]])} onChange={(v) => setKind(v as UploadKind)} />}
          <div class="field">
            <label for="upload-file">File</label>
            <input id="upload-file" type="file" accept={chosen ? data.accept?.[chosen] : ""} disabled={progress !== null}
              onChange={(e) => setFile((e.target as HTMLInputElement).files?.[0] ?? null)} />
            {chosen && data.limits && <small class="muted">
              Up to {Math.round(data.limits[chosen].maxBytes / 1048576)} MB, {chosen === "Image" ? "animations " : ""}up
              to {data.limits[chosen].maxSeconds} seconds: {data.accept?.[chosen]?.split(",").join(" ")}</small>}
          </div>
          <div class="form-row">
            <span class="muted" role="status">{progress ?? ""}</span>
            <button type="submit" class="primary" disabled={!file || progress !== null}>Upload</button>
          </div>
        </form>
      )}
      {(["Sound", "Image", "Video"] as UploadKind[]).map((k) => {
        const list = (data?.items ?? []).filter((i) => i.kind === k);
        return list.length === 0 ? null : (
          <section class="card" key={k}>
            <h2>{KIND_NAMES[k]}</h2>
            <ul class="rows">
              {list.map((i) => (
                <li key={i.id}>
                  <strong>{i.name}</strong>
                  <span class="muted">{describe(i)} · {i.by}</span>
                  {i.canDelete && <span class="row-buttons"><button type="button" class="danger" onClick={() => void remove(i)}>Remove</button></span>}
                </li>
              ))}
            </ul>
          </section>
        );
      })}
      {data?.ok && (data.items ?? []).length === 0 && <p class="muted">Nothing uploaded yet.</p>}
    </>
  );
}
