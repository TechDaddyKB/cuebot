// Channel point rewards on Twitch and Kick (PortalRewards.cs). Changes follow the mod's role: live, or waiting for the streamer.

import { useState } from "preact/hooks";
import { changeMessage, useLoad, usePortal, type Answer } from "../api.js";
import { Check, Field, LoadError } from "../widgets.js";

interface Reward {
  id: string;
  platform: string;
  title: string;
  description: string;
  cost: number;
  color: string;
  enabled: boolean;
  paused: boolean;
  userInput: boolean;
  skipQueue: boolean;
  maxPerStream: number;
  maxPerUserPerStream: number;
  cooldownSeconds: number;
  manageable: boolean | null;
}

interface Platform {
  platform: string;
  name: string;
  ready: boolean;
  max: number;
  used?: number;
  maxTitle: number;
  maxDescription: number;
  streamLimits: boolean;
  cooldown: boolean;
  rewards?: Reward[];
  error?: string;
}

interface Rewards extends Answer { platforms?: Platform[] }

const blank = (platform: string): Reward => ({
  id: "", platform, title: "", description: "", cost: 100, color: platform === "kick" ? "#00E701" : "#9146FF", enabled: true, paused: false,
  userInput: false, skipQueue: false, maxPerStream: 0, maxPerUserPerStream: 0, cooldownSeconds: 0, manageable: true,
});

const num = (v: string) => Math.max(0, Math.floor(Number(v) || 0));

function Editor({ p, start, done }: { p: Platform; start: Reward; done: (saved: boolean) => void }) {
  const { call, notify } = usePortal();
  const [r, setR] = useState(start);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<Reward>) => setR({ ...r, ...patch });
  const save = async (e: Event) => {
    e.preventDefault();
    setBusy(true);
    const answer = await call("rewards.save", { platform: p.platform, reward: r });
    setBusy(false);
    if (!answer.ok) return;
    notify(changeMessage(answer));
    done(true);
  };
  const isTwitch = p.platform === "twitch";
  return (
    <form class="card form" onSubmit={save}>
      <h2>{r.id ? `Change “${start.title}”` : `New ${p.name} reward`}</h2>
      <Field label={`Title (up to ${p.maxTitle} characters)`} value={r.title} onInput={(v) => set({ title: v.slice(0, p.maxTitle) })} />
      <Field label={`${isTwitch ? "Prompt" : "Description"} (up to ${p.maxDescription} characters)`} multiline value={r.description}
        onInput={(v) => set({ description: v.slice(0, p.maxDescription) })} help="What viewers see when they pick it. If they must type something, say what." />
      <div class="field-row">
        <Field label="Cost (points)" type="number" value={String(r.cost)} onInput={(v) => set({ cost: Math.max(1, num(v)) })} />
        <div class="field">
          <label for="reward-color">Colour</label>
          <input id="reward-color" type="color" value={r.color || "#9146FF"} onInput={(e) => set({ color: (e.target as HTMLInputElement).value.toUpperCase() })} />
        </div>
      </div>
      {p.streamLimits && (
        <div class="field-row">
          <Field label="Max per stream (0 = off)" type="number" value={String(r.maxPerStream)} onInput={(v) => set({ maxPerStream: num(v) })} />
          <Field label="Max per viewer per stream (0 = off)" type="number" value={String(r.maxPerUserPerStream)} onInput={(v) => set({ maxPerUserPerStream: num(v) })} />
        </div>
      )}
      {p.cooldown && <Field label="Cooldown in seconds (0 = off)" type="number" value={String(r.cooldownSeconds)} onInput={(v) => set({ cooldownSeconds: num(v) })} />}
      <Check label="Enabled" checked={r.enabled} onChange={(v) => set({ enabled: v })} help="Unticked: hidden from viewers, not deleted." />
      {r.id && <Check label="Paused" checked={r.paused} onChange={(v) => set({ paused: v })} help="Viewers see it but cannot redeem it for now." />}
      <Check label="Viewer must type something" checked={r.userInput} onChange={(v) => set({ userInput: v })} />
      <Check label="Skip the request queue" checked={r.skipQueue} onChange={(v) => set({ skipQueue: v })} help="Marks redemptions done straight away." />
      <div class="form-row">
        <button type="button" onClick={() => done(false)}>Cancel</button>
        <button type="submit" class="primary" disabled={busy || !r.title.trim()}>Save to {p.name}</button>
      </div>
    </form>
  );
}

export function RewardsPage() {
  const { call, notify } = usePortal();
  const [data, reload] = useLoad<Rewards>("rewards.list");
  const [editing, setEditing] = useState<{ p: Platform; r: Reward } | null>(null);

  const remove = async (p: Platform, r: Reward) => {
    if (!confirm(`Delete the ${p.name} reward “${r.title}”? Actions that use it will stop starting.`)) return;
    const answer = await call("rewards.delete", { platform: p.platform, id: r.id });
    if (answer.ok) notify(changeMessage(answer));
    reload();
  };

  if (editing) return <Editor p={editing.p} start={editing.r} done={(saved) => { setEditing(null); if (saved) reload(); }} />;

  return (
    <>
      <h1>Rewards</h1>
      <LoadError data={data} />
      <p class="muted">Channel point rewards on Twitch and Kick. Each platform only lets the app that made a reward change it, so rewards made on
        the Twitch or Kick website are shown but can only be changed there. They still start the streamer's actions.</p>
      {data?.ok && (data.platforms ?? []).map((p) => {
        const full = (p.used ?? 0) >= p.max;
        return (
          <section class="card" key={p.platform}>
            <div class="form-row">
              <h2>{p.name}</h2>
              {p.ready && !p.error && <span class={`badge${full ? " full" : ""}`}>{p.used ?? 0} of {p.max} rewards used</span>}
            </div>
            {!p.ready && <p class="muted">The streamer is not signed in to {p.name}.</p>}
            {p.error && <p class="load-error">{p.error}</p>}
            {p.ready && !p.error && (
              <>
                {(p.rewards ?? []).length === 0 ? <p class="muted">No rewards yet.</p> : (
                  <ul class="rows">
                    {(p.rewards ?? []).map((r) => (
                      <li key={r.id}>
                        <strong>{r.title}</strong>
                        <span class="muted">
                          {r.cost} points{r.enabled ? (r.paused ? " · paused" : "") : " · disabled"}
                          {r.manageable === false ? ` · made outside Cuebot` : ""}
                        </span>
                        {r.manageable !== false && (
                          <span class="row-buttons">
                            <button type="button" onClick={() => setEditing({ p, r })}>Change</button>
                            <button type="button" class="danger" onClick={() => void remove(p, r)}>Delete</button>
                          </span>
                        )}
                      </li>
                    ))}
                  </ul>
                )}
                <div class="form-row">
                  <span class="muted">{full ? `${p.name} allows ${p.max} rewards per channel. Delete one to add another.` : ""}</span>
                  <button type="button" class="primary" disabled={full} onClick={() => setEditing({ p, r: blank(p.platform) })}>+ Reward</button>
                </div>
              </>
            )}
          </section>
        );
      })}
    </>
  );
}
