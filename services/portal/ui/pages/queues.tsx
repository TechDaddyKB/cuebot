import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Queue { name: string; default: boolean; paused: boolean; pending: number; running: number }
interface Queues extends Answer { queues?: Queue[] }

export function QueuesPage() {
  const { call, notify } = usePortal();
  const [data, reload] = useLoad<Queues>("queues.list", 3000);
  const control = async (queue: string, command: "pause" | "resume" | "clear") => {
    const answer = await call("queues.control", { queue, command });
    if (answer.ok && command === "clear") notify(`Removed ${answer.cleared} waiting run(s).`);
    reload();
  };
  return (
    <>
      <h1>Queues</h1>
      <LoadError data={data} />
      <p class="muted">Actions in a queue run one after another. Pause a queue to hold them, or clear what is waiting.</p>
      {data?.queues && (
        <ul class="rows">
          {data.queues.map((q) => (
            <li key={q.name}>
              <strong>{q.default ? "Default" : q.name}</strong>
              <span class="muted">{q.paused ? "Paused · " : ""}{q.pending} waiting{q.running ? `, ${q.running} running` : ""}</span>
              <span class="row-buttons">
                {q.paused
                  ? <button type="button" onClick={() => void control(q.name, "resume")}>Resume</button>
                  : <button type="button" onClick={() => void control(q.name, "pause")}>Pause</button>}
                <button type="button" disabled={q.pending === 0} onClick={() => void control(q.name, "clear")}>Clear</button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
