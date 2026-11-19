import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Input { name: string; db: number; muted: boolean }
interface Audio extends Answer { inputs?: Input[] }

export function AudioPage() {
  const { call } = usePortal();
  const [data, reload] = useLoad<Audio>("audio.list", 5000);
  const set = async (input: string, change: { db?: number; muted?: boolean }) => {
    await call("audio.set", { input, ...change });
    reload();
  };
  return (
    <>
      <h1>Audio</h1>
      <LoadError data={data} />
      <p class="muted">Volume and mute for the streamer's audio sources in OBS. Changes are heard on stream at once.</p>
      {data?.inputs && (data.inputs.length === 0 ? <p class="muted">No audio sources in OBS.</p> : (
        <ul class="rows mixer">
          {data.inputs.map((i) => (
            <li key={i.name}>
              <strong class="mixer-name">{i.name}</strong>
              <input type="range" min={-60} max={0} step={0.5} value={Math.max(-60, i.db)} aria-label={`${i.name} volume`}
                onChange={(e) => void set(i.name, { db: Number((e.target as HTMLInputElement).value) })} />
              <span class="muted mixer-db">{i.db <= -60 ? "-∞" : `${i.db.toFixed(1)} dB`}</span>
              <button type="button" class={i.muted ? "danger" : ""} aria-pressed={i.muted} onClick={() => void set(i.name, { muted: !i.muted })}>
                {i.muted ? "Muted" : "Mute"}
              </button>
            </li>
          ))}
        </ul>
      ))}
    </>
  );
}
