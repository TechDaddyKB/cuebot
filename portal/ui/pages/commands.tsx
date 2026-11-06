import { useState } from "preact/hooks";
import { changeMessage, useLoad, usePortal, type Answer } from "../api.js";
import { Check, Field, Select, LoadError } from "../widgets.js";

interface Command {
  id: string;
  name: string;
  group: string;
  enabled: boolean;
  mode: "Basic" | "Regex";
  location: "Start" | "Exact" | "Anywhere";
  commands: string[];
  caseSensitive: boolean;
  globalCooldownSeconds: number;
  userCooldownSeconds: number;
  minimumRole: "Everyone" | "Subscriber" | "Vip" | "Moderator" | "Broadcaster";
  [key: string]: unknown;
}
interface Commands extends Answer { commands?: Command[] }

const blank = (): Command => ({
  id: crypto.randomUUID(), name: "New command", group: "", enabled: true, mode: "Basic", location: "Start", commands: ["!new"],
  caseSensitive: false, globalCooldownSeconds: 0, userCooldownSeconds: 0, minimumRole: "Everyone",
});

export function CommandsPage() {
  const { can } = usePortal();
  const [data, reload] = useLoad<Commands>("commands.list");
  const [editing, setEditingState] = useState<{ command: Command; isNew: boolean } | null>(null);
  const setEditing = (e: typeof editing) => {
    setEditingState(e);
    window.scrollTo(0, 0);
  };
  if (editing) return <CommandEditor {...editing} done={() => { setEditing(null); reload(); }} />;
  return (
    <>
      <div class="page-head">
        <h1>Commands</h1>
        {can("CreateCommands") && <button type="button" class="primary" onClick={() => setEditing({ command: blank(), isNew: true })}>+ Command</button>}
      </div>
      <LoadError data={data} />
      <p class="muted">Chat commands like !discord. A command starts the actions that use it as their trigger.</p>
      {data?.commands && (data.commands.length === 0 ? <p class="muted">No commands yet.</p> : (
        <ul class="rows">
          {data.commands.map((c) => (
            <li key={c.id}>
              <strong>{c.commands.join(", ") || c.name}</strong>
              <span class="muted">{c.name}{c.enabled ? "" : " · off"}{c.minimumRole !== "Everyone" ? ` · ${c.minimumRole}+` : ""}</span>
              {(can("EditCommands") || can("DeleteCommands")) && (
                <span class="row-buttons"><button type="button" onClick={() => setEditing({ command: structuredClone(c), isNew: false })}>Edit</button></span>
              )}
            </li>
          ))}
        </ul>
      ))}
    </>
  );
}

function CommandEditor({ command, isNew, done }: { command: Command; isNew: boolean; done: () => void }) {
  const { can, call, notify } = usePortal();
  const [c, setC] = useState(command);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const set = (change: Partial<Command>) => setC({ ...c, ...change });
  const save = async (e: Event) => {
    e.preventDefault();
    const answer = await call("commands.save", { command: c });
    if (answer.ok) {
      notify(changeMessage(answer));
      done();
    }
  };
  const remove = async () => {
    const answer = await call("commands.delete", { id: c.id });
    if (answer.ok) {
      notify(answer.status === "pending" ? "Deleting it needs the streamer's approval." : "Deleted.");
      done();
    }
  };
  return (
    <form onSubmit={save}>
      <div class="page-head">
        <h1>{c.name}</h1>
        <button type="button" class="link" onClick={done}>Back</button>
      </div>
      <section class="card form">
        <Field label="Name" value={c.name} onInput={(name) => set({ name })} />
        <Field label="Commands, one per line" multiline value={c.commands.join("\n")}
          onInput={(v) => set({ commands: v.split("\n").map((x) => x.trim()).filter(Boolean) })} />
        <Select label="Match" value={c.location} onChange={(location) => set({ location: location as Command["location"] })}
          options={[["Start", "At the start of a message"], ["Exact", "The whole message"], ["Anywhere", "Anywhere in a message"]]} />
        <Select label="Who can use it" value={c.minimumRole} onChange={(minimumRole) => set({ minimumRole: minimumRole as Command["minimumRole"] })}
          options={[["Everyone", "Everyone"], ["Subscriber", "Subscribers and up"], ["Vip", "VIPs and up"], ["Moderator", "Moderators and up"], ["Broadcaster", "Only the streamer"]]} />
        <Field label="Cooldown for everyone (seconds)" type="number" value={String(c.globalCooldownSeconds)}
          onInput={(v) => set({ globalCooldownSeconds: Math.max(0, Number(v) || 0) })} />
        <Field label="Cooldown per viewer (seconds)" type="number" value={String(c.userCooldownSeconds)}
          onInput={(v) => set({ userCooldownSeconds: Math.max(0, Number(v) || 0) })} />
        <Field label="Group" value={c.group} onInput={(group) => set({ group })} />
        <Check label="On" checked={c.enabled} onChange={(enabled) => set({ enabled })} />
        <Check label="Match upper and lower case exactly" checked={c.caseSensitive} onChange={(caseSensitive) => set({ caseSensitive })} />
      </section>
      <div class="form-row">
        {can("DeleteCommands") && !isNew && (confirmDelete
          ? <span class="row-buttons"><button type="button" class="danger" onClick={() => void remove()}>Yes, delete</button>
            <button type="button" onClick={() => setConfirmDelete(false)}>Keep it</button></span>
          : <button type="button" class="danger" onClick={() => setConfirmDelete(true)}>Delete</button>)}
        <button type="submit" class="primary">Save</button>
      </div>
    </form>
  );
}
