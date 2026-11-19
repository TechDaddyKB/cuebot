import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Sounds extends Answer { sounds?: { id: string; name: string; action: string }[] }

export function SoundsPage() {
  const { call, notify } = usePortal();
  const [data] = useLoad<Sounds>("sounds.list");
  const play = async (id: string, name: string) => {
    if ((await call("sounds.play", { id })).ok) notify(`Playing "${name}" on stream.`);
  };
  return (
    <>
      <h1>Sounds</h1>
      <LoadError data={data} />
      <p class="muted">The sounds the streamer's actions use. Playing one plays it on stream, through the streamer's speakers.</p>
      {data?.sounds && (data.sounds.length === 0 ? <p class="muted">No sounds yet.</p> : (
        <div class="grid buttons">
          {data.sounds.map((s) => (
            <button type="button" key={s.id} onClick={() => void play(s.id, s.name)} title={`From "${s.action}"`}>
              <span aria-hidden="true">▶ </span>{s.name}
            </button>
          ))}
        </div>
      ))}
    </>
  );
}
