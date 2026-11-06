import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Scenes extends Answer { scenes?: string[]; current?: string }

export function ScenesPage() {
  const { call } = usePortal();
  const [data, reload] = useLoad<Scenes>("scenes.list", 5000);
  const go = async (scene: string) => {
    await call("scenes.set", { scene });
    reload();
  };
  return (
    <>
      <h1>Scenes</h1>
      <LoadError data={data} />
      <p class="muted">Switches the streamer's OBS scene. Viewers see the change straight away.</p>
      {data?.scenes && (
        <div class="grid buttons">
          {data.scenes.map((s) => (
            <button type="button" key={s} class={s === data.current ? "primary" : ""} aria-pressed={s === data.current} onClick={() => void go(s)}>
              {s}
            </button>
          ))}
        </div>
      )}
    </>
  );
}
