import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Change {
  id: string;
  at: string;
  area: "action" | "command";
  targetName: string;
  kind: "Create" | "Update" | "Delete";
  status: "Applied" | "Pending" | "Rejected" | "Expired" | "Undone";
}
interface Changes extends Answer { changes?: Change[] }

const STATUS: Record<Change["status"], string> = {
  Applied: "Live",
  Pending: "Waiting for approval",
  Rejected: "Not approved",
  Expired: "Expired before approval",
  Undone: "Undone",
};

const VERB: Record<Change["kind"], string> = { Create: "Added", Update: "Changed", Delete: "Deleted" };

export function ChangesPage() {
  const { call, notify } = usePortal();
  const [data, reload] = useLoad<Changes>("changes.mine");
  const undo = async (id: string) => {
    if ((await call("changes.undo", { id })).ok) notify("Undone.");
    reload();
  };
  const list = [...(data?.changes ?? [])].reverse();
  return (
    <>
      <h1>My changes</h1>
      <LoadError data={data} />
      <p class="muted">Everything you changed, newest first. You can undo a change that is live.</p>
      {data?.ok && (list.length === 0 ? <p class="muted">No changes yet.</p> : (
        <ul class="rows">
          {list.map((c) => (
            <li key={c.id}>
              <span><strong>{VERB[c.kind]} {c.area}</strong> “{c.targetName}”</span>
              <span class={`muted status-${c.status.toLowerCase()}`}>{STATUS[c.status]} · {new Date(c.at).toLocaleString()}</span>
              {c.status === "Applied" && <span class="row-buttons"><button type="button" onClick={() => void undo(c.id)}>Undo</button></span>}
            </li>
          ))}
        </ul>
      ))}
    </>
  );
}
