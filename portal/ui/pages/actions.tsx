import { createContext } from "preact";
import { useContext, useEffect, useState } from "preact/hooks";
import { changeMessage, confirmRun, LEVELS, useLoad, usePortal, type Answer } from "../api.js";
import { Check, Field, Select, LoadError } from "../widgets.js";
import { describe, type UploadItem, type UploadKind, type Uploads } from "./uploads.js";

interface Summary { id: string; name: string; group: string; enabled: boolean; steps: number; locked: boolean; risk?: number }
interface Param {
  key: string; label: string; kind: "Text" | "Multiline" | "Number" | "Boolean" | "Choice" | "Code"; default: string; choices: string[]; help: string;
  /** A file setting: the upload kinds it takes (empty: the folder of uploaded sounds). */
  upload?: UploadKind[];
}
interface StepType { type: string; name: string; category: string; risk: number; branches: string[]; parameters: Param[] }
interface TriggerType { type: string; name: string; category: string; parameters: Param[] }
interface Catalog extends Answer { steps: StepType[]; triggers: TriggerType[] }

interface Step { id: string; type: string; enabled: boolean; parameters: Record<string, string>; branches: Record<string, Step[]>; weight: number; [k: string]: unknown }
interface Trigger { id: string; type: string; enabled: boolean; settings: Record<string, string>; alwaysRun: boolean; [k: string]: unknown }
interface Action {
  id: string; name: string; group: string; enabled: boolean; queue: string; triggers: Trigger[]; subActions: Step[]; [k: string]: unknown;
}

const newAction = (): Action => ({
  id: crypto.randomUUID(), name: "New action", group: "", enabled: true, queue: "", concurrent: false, randomAction: false,
  concurrentSubActions: false, alwaysRun: false, excludeFromHistory: false, triggers: [], subActions: [],
});

const defaults = (params: Param[]) => Object.fromEntries(params.map((p) => [p.key, p.default ?? ""]));

export function ActionsPage() {
  const { me, can, call, notify } = usePortal();
  const [list, reload] = useLoad<Answer & { actions?: Summary[] }>("actions.list");
  const [editing, setEditingState] = useState<{ action: Action; isNew: boolean } | null>(null);
  const setEditing = (e: typeof editing) => {
    setEditingState(e);
    window.scrollTo(0, 0);
  };
  const [filter, setFilter] = useState("");
  const edit = async (id: string) => {
    const answer = await call("actions.get", { id });
    if (answer.ok) setEditing({ action: answer.action as Action, isNew: false });
  };
  const run = async (a: Summary) => {
    if (!confirmRun(a.name, a.risk)) return;
    if ((await call("actions.run", { id: a.id })).ok) notify(`Started "${a.name}".`);
  };
  if (editing) return <ActionEditor {...editing} done={() => { setEditing(null); reload(); }} />;

  const shown = (list?.actions ?? []).filter((a) => `${a.name} ${a.group}`.toLowerCase().includes(filter.toLowerCase()));
  const groups = [...new Set(shown.map((a) => a.group))].sort();
  return (
    <>
      <div class="page-head">
        <h1>Actions</h1>
        {can("CreateActions") && <button type="button" class="primary" onClick={() => setEditing({ action: newAction(), isNew: true })}>+ Action</button>}
      </div>
      <LoadError data={list} />
      <input type="search" class="search" placeholder="Find an action" aria-label="Find an action" value={filter}
        onInput={(e) => setFilter((e.target as HTMLInputElement).value)} />
      {groups.map((g) => (
        <section class="card" key={g}>
          <h2>{g || "No group"}</h2>
          <ul class="rows">
            {shown.filter((a) => a.group === g).map((a) => (
              <li key={a.id}>
                <strong>{a.name}</strong>
                <span class="muted">{a.steps} step{a.steps === 1 ? "" : "s"}{a.enabled ? "" : " · off"}{a.locked ? " · only the streamer can change it" : ""}</span>
                <span class="row-buttons">
                  {can("RunActions") && ((a.risk ?? 0) <= me.maxRisk
                    ? <button type="button" onClick={() => void run(a)}>Run</button>
                    : <span class="chip" title="Running it needs the level of its riskiest step">Can't run</span>)}
                  {!a.locked && (can("EditActions") || can("DeleteActions")) && <button type="button" onClick={() => void edit(a.id)}>Edit</button>}
                </span>
              </li>
            ))}
          </ul>
        </section>
      ))}
      {list?.ok && shown.length === 0 && <p class="muted">No actions{filter ? " match" : " yet"}.</p>}
    </>
  );
}

function ActionEditor({ action, isNew, done }: { action: Action; isNew: boolean; done: () => void }) {
  const { me, can, call, notify, host } = usePortal();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [a, setA] = useState(action);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  useEffect(() => void call("steps.catalog").then((c) => c.ok && setCatalog(c as Catalog)), []);
  // Quietly: a role without upload permissions still edits, with no uploads to pick.
  useEffect(() => void host.request("uploads.list").then((u) => u.ok && setUploads((u as Uploads).items ?? [])).catch(() => {}), []);
  const set = (change: Partial<Action>) => setA({ ...a, ...change });

  const save = async (e: Event) => {
    e.preventDefault();
    const answer = await call("actions.save", { action: a });
    if (answer.ok) {
      notify(changeMessage(answer));
      done();
    }
  };
  const remove = async () => {
    const answer = await call("actions.delete", { id: a.id });
    if (answer.ok) {
      notify(answer.status === "pending" ? "Deleting it needs the streamer's approval." : "Deleted.");
      done();
    }
  };
  if (!catalog) return <p class="muted">Loading…</p>;

  return (
    <UploadsContext.Provider value={uploads}>
    <form onSubmit={save}>
      <div class="page-head">
        <h1>{a.name}</h1>
        <button type="button" class="link" onClick={done}>Back</button>
      </div>
      <p class="muted">You can add steps up to level {me.maxRisk} ({LEVELS[me.maxRisk]}). Steps above that are shown but only the streamer can change them.</p>
      <section class="card form">
        <Field label="Name" value={a.name} onInput={(name) => set({ name })} />
        <Field label="Group" value={a.group} onInput={(group) => set({ group })} />
        <Check label="On" checked={a.enabled} onChange={(enabled) => set({ enabled })} />
      </section>

      <section class="card">
        <h2>Starts when</h2>
        <Triggers triggers={a.triggers} catalog={catalog} onChange={(triggers) => set({ triggers })} />
      </section>

      <section class="card">
        <h2>Steps</h2>
        <Steps steps={a.subActions} catalog={catalog} onChange={(subActions) => set({ subActions })} />
      </section>

      <div class="form-row">
        {can("DeleteActions") && !isNew && (confirmDelete
          ? <span class="row-buttons"><button type="button" class="danger" onClick={() => void remove()}>Yes, delete</button>
            <button type="button" onClick={() => setConfirmDelete(false)}>Keep it</button></span>
          : <button type="button" class="danger" onClick={() => setConfirmDelete(true)}>Delete</button>)}
        <button type="submit" class="primary">Save</button>
      </div>
    </form>
    </UploadsContext.Provider>
  );
}

const UploadsContext = createContext<UploadItem[]>([]);

/** A file setting: uploads of the right kind, or the file the streamer chose (kept as it is). */
function UploadPicker({ param, value, onChange }: { param: Param; value: string; onChange: (v: string) => void }) {
  const uploads = useContext(UploadsContext);
  const kinds = param.upload ?? [];
  const options: [string, string][] = [["", "None"]];
  if (kinds.length === 0) options.push(["upload:sounds", "All uploaded sounds"]);
  else for (const u of uploads.filter((u) => kinds.includes(u.kind))) options.push([`upload:${u.id}`, `${u.name} (${describe(u)})`]);
  if (value && !options.some(([v]) => v === value))
    options.push([value, value.startsWith("upload:") ? "A removed upload" : "The streamer's own file"]);
  return <Select label={param.label} help={param.help} value={value} options={options} onChange={onChange} />;
}

function Params({ params, values, onChange }: { params: Param[]; values: Record<string, string>; onChange: (v: Record<string, string>) => void }) {
  const set = (key: string, value: string) => onChange({ ...values, [key]: value });
  return (
    <div class="form">
      {params.map((p) => {
        const value = values[p.key] ?? p.default ?? "";
        if (p.upload) return <UploadPicker key={p.key} param={p} value={value} onChange={(v) => set(p.key, v)} />;
        switch (p.kind) {
          case "Boolean":
            return <Check key={p.key} label={p.label} help={p.help} checked={value === "true"} onChange={(v) => set(p.key, String(v))} />;
          case "Choice":
            return <Select key={p.key} label={p.label} help={p.help} value={value} options={p.choices.map((c) => [c, c])} onChange={(v) => set(p.key, v)} />;
          case "Number":
            return <Field key={p.key} label={p.label} help={p.help} type="number" value={value} onInput={(v) => set(p.key, v)} />;
          case "Multiline":
          case "Code":
            return <Field key={p.key} label={p.label} help={p.help} multiline mono={p.kind === "Code"} value={value} onInput={(v) => set(p.key, v)} />;
          default:
            return <Field key={p.key} label={p.label} help={p.help} value={value} onInput={(v) => set(p.key, v)} />;
        }
      })}
    </div>
  );
}

/** A type picker grouped by category, with an Add button. */
function AddPicker({ label, types, onAdd }: { label: string; types: { type: string; name: string; category: string }[]; onAdd: (type: string) => void }) {
  const [type, setType] = useState("");
  const categories = [...new Set(types.map((t) => t.category))].sort();
  return (
    <div class="add-picker">
      <select aria-label={label} value={type} onChange={(e) => setType((e.target as HTMLSelectElement).value)}>
        <option value="">{label}…</option>
        {categories.map((c) => (
          <optgroup key={c} label={c}>
            {types.filter((t) => t.category === c).sort((x, y) => x.name.localeCompare(y.name)).map((t) => <option key={t.type} value={t.type}>{t.name}</option>)}
          </optgroup>
        ))}
      </select>
      <button type="button" disabled={!type} onClick={() => { onAdd(type); setType(""); }}>Add</button>
    </div>
  );
}

function move<T>(list: T[], i: number, by: number): T[] {
  const j = i + by;
  if (j < 0 || j >= list.length) return list;
  const copy = [...list];
  [copy[i], copy[j]] = [copy[j]!, copy[i]!];
  return copy;
}

function Triggers({ triggers, catalog, onChange }: { triggers: Trigger[]; catalog: Catalog; onChange: (t: Trigger[]) => void }) {
  const known = (type: string) => catalog.triggers.find((t) => t.type === type);
  return (
    <>
      {triggers.length === 0 && <p class="muted">No triggers: it only runs when someone runs it.</p>}
      {triggers.map((t, i) => {
        const def = known(t.type);
        const update = (change: Partial<Trigger>) => onChange(triggers.map((x, k) => (k === i ? { ...x, ...change } : x)));
        return (
          <div class={`item ${t.enabled ? "" : "off"}`} key={t.id}>
            <div class="item-head">
              <strong>{def?.name ?? t.type}</strong>
              {def
                ? <span class="row-buttons">
                    <button type="button" class="small" aria-pressed={t.enabled} onClick={() => update({ enabled: !t.enabled })}>{t.enabled ? "On" : "Off"}</button>
                    <button type="button" class="small danger" onClick={() => onChange(triggers.filter((_, k) => k !== i))}>Remove</button>
                  </span>
                : <span class="muted">Only the streamer can change this trigger.</span>}
            </div>
            {def && def.parameters.length > 0 && <Params params={def.parameters} values={t.settings} onChange={(settings) => update({ settings })} />}
          </div>
        );
      })}
      <AddPicker label="Add a trigger" types={catalog.triggers}
        onAdd={(type) => onChange([...triggers, { id: crypto.randomUUID(), type, enabled: true, alwaysRun: false, settings: defaults(known(type)!.parameters) }])} />
    </>
  );
}

function Steps({ steps, catalog, onChange }: { steps: Step[]; catalog: Catalog; onChange: (s: Step[]) => void }) {
  const known = (type: string) => catalog.steps.find((s) => s.type === type);
  return (
    <div class="steps">
      {steps.map((s, i) => {
        const def = known(s.type);
        const update = (change: Partial<Step>) => onChange(steps.map((x, k) => (k === i ? { ...x, ...change } : x)));
        const branches = def ? def.branches : Object.keys(s.branches ?? {});
        return (
          <div class={`item ${s.enabled ? "" : "off"} ${def ? "" : "locked"}`} key={s.id}>
            <div class="item-head">
              <span class="step-number">{i + 1}</span>
              <strong>{def?.name ?? s.type}</strong>
              {def && <span class="chip">{LEVELS[def.risk]}</span>}
              {def
                ? <span class="row-buttons">
                    <button type="button" class="small" aria-label="Move up" disabled={i === 0} onClick={() => onChange(move(steps, i, -1))}>↑</button>
                    <button type="button" class="small" aria-label="Move down" disabled={i === steps.length - 1} onClick={() => onChange(move(steps, i, 1))}>↓</button>
                    <button type="button" class="small" aria-pressed={s.enabled} onClick={() => update({ enabled: !s.enabled })}>{s.enabled ? "On" : "Off"}</button>
                    <button type="button" class="small danger" onClick={() => onChange(steps.filter((_, k) => k !== i))}>Remove</button>
                  </span>
                : <span class="muted">Only the streamer can change this step.</span>}
            </div>
            {def && def.parameters.length > 0 && <Params params={def.parameters} values={s.parameters} onChange={(parameters) => update({ parameters })} />}
            {def && branches.map((b) => (
              <div class="branch" key={b}>
                <div class="branch-name">{b}</div>
                <Steps steps={s.branches?.[b] ?? []} catalog={catalog} onChange={(list) => update({ branches: { ...s.branches, [b]: list } })} />
              </div>
            ))}
          </div>
        );
      })}
      <AddPicker label="Add a step" types={catalog.steps}
        onAdd={(type) => onChange([...steps, { id: crypto.randomUUID(), type, enabled: true, weight: 1, branches: {}, parameters: defaults(known(type)!.parameters) }])} />
    </div>
  );
}
