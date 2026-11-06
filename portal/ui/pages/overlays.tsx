import { useEffect, useRef, useState } from "preact/hooks";
import { changeMessage, useLoad, usePortal, type Answer } from "../api.js";
import { Check, Field, LoadError, Select } from "../widgets.js";
import { describe, type UploadItem, type UploadKind, type Uploads } from "./uploads.js";

interface Animation { name: string; duration: number }

interface Layer {
  id: string;
  name: string;
  type: string;
  elementId?: string | null;
  /** A new layer of one of Cuebot's widgets (made when the change goes live). */
  widget?: string;
  x: number;
  y: number;
  width: number;
  height: number;
  opacity: number;
  rotation: number;
  visible: boolean;
  locked: boolean;
  enter: Animation;
  exit: Animation;
  settings?: Record<string, unknown>;
}

interface Overlay { id: string; name: string; width: number; height: number; layers: Layer[] }

interface WidgetField {
  key: string;
  type: string;
  label: string;
  group: string;
  value: unknown;
  options?: Record<string, string> | null;
  min?: number | null;
  max?: number | null;
  step?: number | null;
  editable: boolean;
  why?: string | null;
}

type Design = Record<string, unknown>;
interface AlertType { enabled: boolean; design: Design; variations?: { name?: string; enabled?: boolean; design?: Design }[] }
interface Alerts { types: Record<string, AlertType>; randomPick?: boolean; [key: string]: unknown }

interface Widget { id: string; name: string; kind?: string | null; usedBy: string[]; fields: WidgetField[]; alerts?: Alerts }

interface OverlayAnswer extends Answer {
  overlay?: Overlay;
  widgets?: Widget[];
  canCreate?: boolean;
  enter?: string[];
  exit?: string[];
  add?: { static: string[]; widgets: { kind: string; name: string }[]; library: { id: string; name: string }[] } | null;
}

interface ListAnswer extends Answer {
  overlays?: { id: string; name: string; width: number; height: number; layers: number }[];
  canCreate?: boolean;
}

const SIZES: [string, string][] = [["1920x1080", "1920 × 1080 (1080p)"], ["2560x1440", "2560 × 1440"], ["3840x2160", "3840 × 2160 (4K)"],
  ["1080x1920", "1080 × 1920 (vertical)"], ["1280x720", "1280 × 720"]];
const TYPE_NAMES: Record<string, string> = { text: "Text", image: "Image", video: "Video", slideshow: "Slideshow", custom: "Widget" };
const ALERT_NAMES: Record<string, string> = {
  follower: "Follow", subscriber: "Subscription", tip: "Tip", cheer: "Cheer (bits)", raid: "Raid", superchat: "Super Chat", kicks: "Kicks",
  redemption: "Reward redemption", jewels: "YouTube Jewels", charityCampaignDonation: "Charity donation", merch: "Merch", custom: "Custom alert",
};
const FITS: [string, string][] = [["contain", "Fit inside"], ["cover", "Fill and crop"], ["fill", "Stretch"]];

export function OverlaysPage() {
  const [open, setOpen] = useState<string | null>(null);
  return open ? <OverlayEditor id={open} done={() => setOpen(null)} /> : <OverlayList onOpen={setOpen} />;
}

function OverlayList({ onOpen }: { onOpen: (id: string) => void }) {
  const { call, notify } = usePortal();
  const [data, reload] = useLoad<ListAnswer>("overlays.list", 10000);
  const [name, setName] = useState("");
  const [size, setSize] = useState("1920x1080");

  const create = async (e: Event) => {
    e.preventDefault();
    const [width, height] = size.split("x").map(Number);
    const answer = await call("overlays.create", { name, width, height });
    if (!answer.ok) return;
    notify(changeMessage(answer));
    setName("");
    reload();
  };
  const remove = async (o: { id: string; name: string }) => {
    if (!confirm(`Delete the overlay "${o.name}"? OBS shows nothing where it was used (the streamer can undo it).`)) return;
    const answer = await call("overlays.delete", { id: o.id });
    if (answer.ok) notify(changeMessage(answer));
    reload();
  };

  return (
    <>
      <h1>Overlays</h1>
      <LoadError data={data} />
      <p class="muted">The streamer's overlays in OBS. Move and restyle layers, change widget settings and alert designs, and use your
        uploads. Saved changes show in OBS straight away.</p>
      {data?.ok && (
        <section class="card">
          <ul class="rows">
            {(data.overlays ?? []).map((o) => (
              <li key={o.id}>
                <strong>{o.name}</strong>
                <span class="muted">{o.width} × {o.height} · {o.layers} {o.layers === 1 ? "layer" : "layers"}</span>
                <span class="row-buttons">
                  <button type="button" onClick={() => onOpen(o.id)}>Edit</button>
                  {data.canCreate && <button type="button" class="danger" onClick={() => void remove(o)}>Delete</button>}
                </span>
              </li>
            ))}
          </ul>
          {(data.overlays ?? []).length === 0 && <p class="muted">The streamer has no overlays yet.</p>}
        </section>
      )}
      {data?.canCreate && (
        <form class="card form" onSubmit={create}>
          <h2>New overlay</h2>
          <Field label="Name" value={name} onInput={setName} />
          <Select label="Size" value={size} options={SIZES} onChange={setSize} />
          <div class="form-row"><span /><button type="submit" class="primary" disabled={!name.trim()}>Create</button></div>
        </form>
      )}
    </>
  );
}

function OverlayEditor({ id, done }: { id: string; done: () => void }) {
  const { call, notify, host } = usePortal();
  const [data, reload] = useLoad<OverlayAnswer>("overlays.get", 0, { id });
  const [overlay, setOverlay] = useState<Overlay | null>(null);
  const [widgets, setWidgets] = useState<Record<string, Widget>>({});
  const [dirtyWidgets, setDirtyWidgets] = useState<Set<string>>(new Set());
  const [dirty, setDirty] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!data?.ok || !data.overlay) return;
    setOverlay(structuredClone(data.overlay));
    setWidgets(Object.fromEntries((data.widgets ?? []).map((w) => [w.id, structuredClone(w)])));
    setDirty(false);
    setDirtyWidgets(new Set());
  }, [data]);
  useEffect(() => {
    void host.request("uploads.list").then((a) => setUploads(((a as Uploads).items ?? [])), () => setUploads([]));
  }, []);

  if (!data) return <p class="muted">Loading…</p>;
  if (!data.ok || !overlay) return <><LoadError data={data} /><button type="button" class="link" onClick={done}>Back</button></>;

  const layer = overlay.layers.find((l) => l.id === selected) ?? null;
  const setLayer = (l: Layer) => {
    setOverlay({ ...overlay, layers: overlay.layers.map((x) => (x.id === l.id ? l : x)) });
    setDirty(true);
  };
  const setWidget = (w: Widget) => {
    setWidgets({ ...widgets, [w.id]: w });
    setDirtyWidgets(new Set([...dirtyWidgets, w.id]));
  };
  const move = (l: Layer, by: number) => {
    const list = [...overlay.layers];
    const i = list.indexOf(l);
    const j = i + by;
    if (j < 0 || j >= list.length) return;
    [list[i], list[j]] = [list[j]!, list[i]!];
    setOverlay({ ...overlay, layers: list });
    setDirty(true);
  };
  const removeLayer = (l: Layer) => {
    if (!confirm(`Remove the layer "${l.name}" from this overlay?`)) return;
    setOverlay({ ...overlay, layers: overlay.layers.filter((x) => x !== l) });
    setSelected(null);
    setDirty(true);
  };
  const add = (choice: string) => {
    if (!choice) return;
    const [kind, value] = choice.split(":") as [string, string];
    const fresh: Layer = {
      id: `new-${crypto.randomUUID()}`, name: kind === "static" ? TYPE_NAMES[value] ?? value : "New widget", type: kind === "static" ? value : "custom",
      x: 0, y: 0, width: Math.min(640, overlay.width), height: Math.min(360, overlay.height), opacity: 1, rotation: 0, visible: true, locked: false,
      enter: { name: "none", duration: 1 }, exit: { name: "none", duration: 1 },
    };
    fresh.x = Math.round((overlay.width - fresh.width) / 2);
    fresh.y = Math.round((overlay.height - fresh.height) / 2);
    if (kind === "widget") { fresh.widget = value; fresh.name = data.add?.widgets.find((w) => w.kind === value)?.name ?? value; }
    if (kind === "library") { fresh.elementId = value; fresh.name = data.add?.library.find((w) => w.id === value)?.name ?? "Widget"; }
    setOverlay({ ...overlay, layers: [...overlay.layers, fresh] });
    setSelected(fresh.id);
    setDirty(true);
  };

  const save = async () => {
    setSaving(true);
    try {
      let last: Answer | null = null;
      if (dirty) {
        // New layers' settings start from Cuebot's defaults; only what the mod changed is sent.
        const body = { ...overlay, layers: overlay.layers.map((l) => (l.id.startsWith("new-") ? { ...l, id: "", settings: l.settings ?? {} } : l)) };
        const answer = await call("overlays.save", { overlay: body });
        if (!answer.ok) return;
        last = answer;
      }
      // The most telling result is shown: waiting for approval, then live, then nothing changed.
      const rank = (a: Answer | null) => (a?.status === "pending" ? 3 : a?.status === "live" ? 2 : a ? 1 : 0);
      for (const wid of dirtyWidgets) {
        const w = widgets[wid]!;
        const values: Record<string, unknown> = Object.fromEntries(w.fields.filter((f) => f.editable).map((f) => [f.key, f.value]));
        if (w.alerts) values.alerts = w.alerts;
        const answer = await call("widgets.save", { id: w.id, data: values });
        if (!answer.ok) return;
        if (rank(answer) >= rank(last)) last = answer;
      }
      if (last) notify(changeMessage(last));
      reload();
    } finally {
      setSaving(false);
    }
  };

  const widget = layer?.elementId ? widgets[layer.elementId] : undefined;
  const changed = dirty || dirtyWidgets.size > 0;

  return (
    <>
      <div class="page-head">
        <h1>{overlay.name}</h1>
        <button type="button" class="link" onClick={() => (!changed || confirm("Leave without saving?")) && done()}>Back</button>
      </div>
      <p class="muted">{overlay.width} × {overlay.height}. Tap a layer to change it; drag it to move it.</p>
      <Canvas overlay={overlay} selected={selected} onSelect={setSelected} onMove={setLayer} />
      <section class="card">
        <h2>Layers</h2>
        <ul class="rows layers">
          {[...overlay.layers].reverse().map((l) => (
            <li key={l.id} class={l.id === selected ? "selected" : ""}>
              <button type="button" class="link" onClick={() => setSelected(l.id)}>{l.name}</button>
              <span class="muted">{TYPE_NAMES[l.type] ?? l.type}{l.visible ? "" : " · hidden"}{l.locked ? " · locked" : ""}</span>
              <span class="row-buttons">
                <button type="button" class="small" title="Show or hide" onClick={() => setLayer({ ...l, visible: !l.visible })}>{l.visible ? "Hide" : "Show"}</button>
                <button type="button" class="small" title="Bring forward" aria-label={`Bring ${l.name} forward`} onClick={() => move(l, 1)}>▲</button>
                <button type="button" class="small" title="Send back" aria-label={`Send ${l.name} back`} onClick={() => move(l, -1)}>▼</button>
                {data.canCreate && <button type="button" class="small danger" onClick={() => removeLayer(l)}>Remove</button>}
              </span>
            </li>
          ))}
        </ul>
        {data.canCreate && data.add && (
          <div class="add-picker">
            <select aria-label="Add a layer" value="" onChange={(e) => { add((e.target as HTMLSelectElement).value); (e.target as HTMLSelectElement).value = ""; }}>
              <option value="">Add a layer…</option>
              <optgroup label="Layers">{data.add.static.map((t) => <option key={t} value={`static:${t}`}>{TYPE_NAMES[t] ?? t}</option>)}</optgroup>
              <optgroup label="Cuebot widgets">{data.add.widgets.map((w) => <option key={w.kind} value={`widget:${w.kind}`}>{w.name}</option>)}</optgroup>
              {data.add.library.length > 0 && <optgroup label="The streamer's widgets">{data.add.library.map((w) => <option key={w.id} value={`library:${w.id}`}>{w.name}</option>)}</optgroup>}
            </select>
          </div>
        )}
      </section>

      {layer && (
        <LayerForm layer={layer} onChange={setLayer} enter={data.enter ?? []} exit={data.exit ?? []} uploads={uploads} />
      )}
      {layer && widget && <WidgetForm widget={widget} onChange={setWidget} uploads={uploads} />}
      {layer?.type === "custom" && !widget && <p class="muted">This widget's settings can be changed once the layer is saved.</p>}

      <div class="form-row sticky-save">
        <span class="muted">{changed ? "Not saved yet." : ""}</span>
        <button type="button" class="primary" disabled={!changed || saving} onClick={() => void save()}>{saving ? "Saving…" : "Save"}</button>
      </div>
    </>
  );
}

/** The overlay drawn to scale: a box per layer, top layers last. Drag to move (not locked layers). */
function Canvas({ overlay, selected, onSelect, onMove }: { overlay: Overlay; selected: string | null; onSelect: (id: string) => void; onMove: (l: Layer) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: string; startX: number; startY: number; x: number; y: number; scale: number } | null>(null);
  const down = (e: PointerEvent, l: Layer) => {
    onSelect(l.id);
    if (l.locked || !ref.current) return;
    const scale = overlay.width / ref.current.clientWidth;
    drag.current = { id: l.id, startX: e.clientX, startY: e.clientY, x: l.x, y: l.y, scale };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    e.preventDefault();
  };
  const moveTo = (e: PointerEvent, l: Layer) => {
    const d = drag.current;
    if (!d || d.id !== l.id) return;
    onMove({ ...l, x: Math.round(d.x + (e.clientX - d.startX) * d.scale), y: Math.round(d.y + (e.clientY - d.startY) * d.scale) });
  };
  return (
    <div class="canvas" ref={ref} style={{ aspectRatio: `${overlay.width} / ${overlay.height}` }}>
      {overlay.layers.map((l) => (
        <div key={l.id} class={`layer-box${l.id === selected ? " selected" : ""}${l.visible ? "" : " hidden"}`} role="button" tabIndex={0}
          aria-label={l.name}
          style={{
            left: `${(l.x / overlay.width) * 100}%`, top: `${(l.y / overlay.height) * 100}%`,
            width: `${(l.width / overlay.width) * 100}%`, height: `${(l.height / overlay.height) * 100}%`,
            transform: l.rotation ? `rotate(${l.rotation}deg)` : undefined,
          }}
          onPointerDown={(e) => down(e, l)} onPointerMove={(e) => moveTo(e, l)} onPointerUp={() => (drag.current = null)}
          onKeyDown={(e) => e.key === "Enter" && onSelect(l.id)}>
          <span>{l.type === "text" ? String(l.settings?.text ?? l.name) : l.name}</span>
        </div>
      ))}
    </div>
  );
}

function num(v: string, fallback: number): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function LayerForm({ layer, onChange, enter, exit, uploads }: { layer: Layer; onChange: (l: Layer) => void; enter: string[]; exit: string[]; uploads: UploadItem[] }) {
  const set = (patch: Partial<Layer>) => onChange({ ...layer, ...patch });
  const s = layer.settings ?? {};
  const setting = (key: string, value: unknown) => onChange({ ...layer, settings: { ...s, [key]: value } });
  const str = (key: string) => String(s[key] ?? "");
  return (
    <section class="card form">
      <h2>{layer.name}</h2>
      <Field label="Name" value={layer.name} onInput={(name) => set({ name })} />
      <div class="grid four">
        <Field label="Left" type="number" value={String(layer.x)} onInput={(v) => set({ x: num(v, layer.x) })} />
        <Field label="Top" type="number" value={String(layer.y)} onInput={(v) => set({ y: num(v, layer.y) })} />
        <Field label="Width" type="number" value={String(layer.width)} onInput={(v) => set({ width: num(v, layer.width) })} />
        <Field label="Height" type="number" value={String(layer.height)} onInput={(v) => set({ height: num(v, layer.height) })} />
      </div>
      <div class="grid four">
        <Field label="Opacity %" type="number" value={String(Math.round(layer.opacity * 100))} onInput={(v) => set({ opacity: Math.min(1, Math.max(0, num(v, 100) / 100)) })} />
        <Field label="Rotation °" type="number" value={String(layer.rotation)} onInput={(v) => set({ rotation: num(v, layer.rotation) })} />
        <Check label="Shown" checked={layer.visible} onChange={(visible) => set({ visible })} />
        <Check label="Locked" checked={layer.locked} onChange={(locked) => set({ locked })} help="Locked layers cannot be dragged." />
      </div>
      <div class="grid four">
        <Select label="Comes in with" value={layer.enter.name} options={enter.map((a) => [a, a])} onChange={(name) => set({ enter: { ...layer.enter, name } })} />
        <Field label="Seconds" type="number" value={String(layer.enter.duration)} onInput={(v) => set({ enter: { ...layer.enter, duration: num(v, 1) } })} />
        <Select label="Goes out with" value={layer.exit.name} options={exit.map((a) => [a, a])} onChange={(name) => set({ exit: { ...layer.exit, name } })} />
        <Field label="Seconds" type="number" value={String(layer.exit.duration)} onInput={(v) => set({ exit: { ...layer.exit, duration: num(v, 1) } })} />
      </div>
      {layer.type === "text" && <>
        <Field label="Text" multiline value={str("text") || (layer.id.startsWith("new-") ? "Your text here" : "")} onInput={(v) => setting("text", v)} />
        <div class="grid four">
          <Field label="Font" value={str("font") || "Inter"} onInput={(v) => setting("font", v)} />
          <Field label="Size" type="number" value={str("size") || "48"} onInput={(v) => setting("size", num(v, 48))} />
          <Field label="Colour" value={str("color") || "#ffffff"} onInput={(v) => setting("color", v)} />
          <Select label="Weight" value={str("weight") || "700"} options={["300", "400", "500", "600", "700", "800", "900"].map((w) => [w, w])} onChange={(v) => setting("weight", v)} />
        </div>
        <div class="grid four">
          <Select label="Align" value={str("align") || "center"} options={[["left", "Left"], ["center", "Centre"], ["right", "Right"]]} onChange={(v) => setting("align", v)} />
          <Field label="Background (empty: none)" value={str("background")} onInput={(v) => setting("background", v)} />
          <Field label="Padding" type="number" value={str("padding") || "0"} onInput={(v) => setting("padding", num(v, 0))} />
          <Check label="Shadow" checked={s.shadow !== false} onChange={(v) => setting("shadow", v)} />
        </div>
      </>}
      {(layer.type === "image" || layer.type === "video") && <>
        <MediaPicker label={layer.type === "image" ? "Picture" : "Video"} kinds={layer.type === "image" ? ["Image"] : ["Video"]} uploads={uploads}
          value={str("url")} onChange={(v) => setting("url", v)} />
        <Select label="Fit" value={str("fit") || "contain"} options={FITS} onChange={(v) => setting("fit", v)} />
        {layer.type === "video" && <div class="grid four">
          <Check label="Loop" checked={s.loop !== false} onChange={(v) => setting("loop", v)} />
          <Check label="Muted" checked={s.muted !== false} onChange={(v) => setting("muted", v)} />
          <Field label="Volume %" type="number" value={str("volume") || "100"} onInput={(v) => setting("volume", num(v, 100))} />
        </div>}
      </>}
      {layer.type === "slideshow" && <Slides value={(s.urls as string[] | undefined) ?? []} uploads={uploads} onChange={(urls) => setting("urls", urls)}
        seconds={str("seconds") || "8"} onSeconds={(v) => setting("seconds", num(v, 8))} />}
    </section>
  );
}

function Slides({ value, uploads, onChange, seconds, onSeconds }: { value: string[]; uploads: UploadItem[]; onChange: (v: string[]) => void; seconds: string; onSeconds: (v: string) => void }) {
  return (
    <>
      {value.map((url, i) => (
        <div class="add-picker" key={i}>
          <MediaPicker label={`Picture ${i + 1}`} kinds={["Image"]} uploads={uploads} value={url} onChange={(v) => onChange(value.map((x, j) => (j === i ? v : x)))} />
          <button type="button" class="small danger" onClick={() => onChange(value.filter((_, j) => j !== i))}>Remove</button>
        </div>
      ))}
      <div class="form-row">
        <Field label="Seconds per picture" type="number" value={seconds} onInput={onSeconds} />
        <button type="button" onClick={() => onChange([...value, ""])}>Add a picture</button>
      </div>
    </>
  );
}

/** A file setting: uploads of the right kind, or the file the streamer chose (kept as it is). */
function MediaPicker({ label, kinds, uploads, value, onChange, extra = [] }: {
  label: string; kinds: UploadKind[]; uploads: UploadItem[]; value: string; onChange: (v: string) => void; extra?: [string, string][];
}) {
  const options: [string, string][] = [...extra, ["", "None"]];
  for (const u of uploads.filter((u) => kinds.includes(u.kind))) options.push([`upload:${u.id}`, `${u.name} (${describe(u)})`]);
  if (value && !options.some(([v]) => v === value)) options.push([value, value.startsWith("upload:") ? "A removed upload" : "The streamer's own file"]);
  return <Select label={label} value={value} options={options} onChange={onChange} help={uploads.length === 0 ? "Upload files on the Uploads page." : undefined} />;
}

function WidgetForm({ widget, onChange, uploads }: { widget: Widget; onChange: (w: Widget) => void; uploads: UploadItem[] }) {
  const setField = (key: string, value: unknown) => onChange({ ...widget, fields: widget.fields.map((f) => (f.key === key ? { ...f, value } : f)) });
  const groups = [...new Set(widget.fields.map((f) => f.group))];
  return (
    <section class="card form">
      <h2>{widget.name} settings</h2>
      {widget.usedBy.length > 1 && <p class="muted">This widget is also on {widget.usedBy.filter((n, i, a) => a.indexOf(n) === i).join(", ")}: changes show everywhere it is used.</p>}
      {groups.map((g) => (
        <fieldset key={g} class="group">
          {g && <legend>{g}</legend>}
          {widget.fields.filter((f) => f.group === g).map((f) => <WidgetFieldInput key={f.key} field={f} uploads={uploads} onChange={(v) => setField(f.key, v)} />)}
        </fieldset>
      ))}
      {widget.alerts && <AlertDesigns alerts={widget.alerts} uploads={uploads} onChange={(alerts) => onChange({ ...widget, alerts })} />}
    </section>
  );
}

function WidgetFieldInput({ field: f, uploads, onChange }: { field: WidgetField; uploads: UploadItem[]; onChange: (v: unknown) => void }) {
  const value = f.value;
  if (!f.editable) return <div class="field"><label>{f.label}</label><span class="muted">{String(value ?? "")} · {f.why}</span></div>;
  switch (f.type) {
    case "checkbox":
      return <Check label={f.label} checked={value === true} onChange={onChange} />;
    case "dropdown":
      return <Select label={f.label} value={String(value ?? "")} options={Object.entries(f.options ?? {})} onChange={onChange} />;
    case "number":
    case "slider": {
      const range = f.min != null || f.max != null ? `From ${f.min ?? "…"} to ${f.max ?? "…"}.` : undefined;
      return <Field label={f.label} type="number" help={range} value={String(value ?? "")} onInput={(v) => onChange(num(v, Number(value) || 0))} />;
    }
    case "image-input":
    case "video-input":
    case "sound-input":
      return <MediaPicker label={f.label} kinds={[f.type === "image-input" ? "Image" : f.type === "video-input" ? "Video" : "Sound"]} uploads={uploads}
        value={String(value ?? "")} onChange={onChange} />;
    case "textfield":
      return <Field label={f.label} multiline value={String(value ?? "")} onInput={onChange} />;
    case "colorpicker":
      return <Field label={f.label} value={String(value ?? "")} onInput={onChange} help="A colour such as #ff4fa3 or rgba(0, 0, 0, 0.5)." />;
    default:
      return <Field label={f.label} value={String(value ?? "")} onInput={onChange} />;
  }
}

function AlertDesigns({ alerts, uploads, onChange }: { alerts: Alerts; uploads: UploadItem[]; onChange: (a: Alerts) => void }) {
  const setType = (key: string, t: AlertType) => onChange({ ...alerts, types: { ...alerts.types, [key]: t } });
  return (
    <fieldset class="group">
      <legend>Alerts</legend>
      <p class="muted">Each alert's text, picture or video, sound and look. Which variation plays when stays with the streamer.</p>
      {Object.entries(alerts.types).map(([key, t]) => (
        <details key={key} class="item">
          <summary class="item-head"><strong>{ALERT_NAMES[key] ?? key}</strong>{!t.enabled && <span class="chip">off</span>}</summary>
          <Check label="On" checked={t.enabled} onChange={(enabled) => setType(key, { ...t, enabled })} />
          <DesignForm design={t.design} uploads={uploads} onChange={(design) => setType(key, { ...t, design })} />
          {(t.variations ?? []).map((v, i) => (
            <details key={i} class="item">
              <summary class="item-head">{v.name ?? `Variation ${i + 1}`}{v.enabled === false && <span class="chip">off</span>}</summary>
              <Check label="On" checked={v.enabled !== false} onChange={(enabled) =>
                setType(key, { ...t, variations: t.variations!.map((x, j) => (j === i ? { ...x, enabled } : x)) })} />
              {v.design && <DesignForm design={v.design} uploads={uploads} onChange={(design) =>
                setType(key, { ...t, variations: t.variations!.map((x, j) => (j === i ? { ...x, design } : x)) })} />}
            </details>
          ))}
        </details>
      ))}
    </fieldset>
  );
}

const ANIMATIONS_IN = ["none", "fadeIn", "fadeInUp", "fadeInDown", "fadeInLeft", "fadeInRight", "slideInUp", "slideInDown", "slideInLeft", "slideInRight", "zoomIn", "bounceIn"];
const ANIMATIONS_OUT = ["none", "fadeOut", "fadeOutUp", "fadeOutDown", "fadeOutLeft", "fadeOutRight", "slideOutUp", "slideOutDown", "slideOutLeft", "slideOutRight", "zoomOut", "bounceOut"];

function DesignForm({ design: d, uploads, onChange }: { design: Design; uploads: UploadItem[]; onChange: (d: Design) => void }) {
  const set = (key: string, value: unknown) => onChange({ ...d, [key]: value });
  const str = (key: string) => String(d[key] ?? "");
  return (
    <div class="form">
      <Field label="Text" value={str("template")} onInput={(v) => set("template", v)} help="{name}, {amount}, {formattedAmount}, {message}, {tier} and more are filled in." />
      <Check label="Show the viewer's message" checked={d.showMessage === true} onChange={(v) => set("showMessage", v)} />
      <div class="grid four">
        <Field label="Seconds on screen" type="number" value={str("duration")} onInput={(v) => set("duration", num(v, 8))} />
        <Select label="Layout" value={str("layout")} options={[["above", "Picture above text"], ["side", "Picture beside text"], ["overlay", "Text over picture"]]} onChange={(v) => set("layout", v)} />
        <Select label="Comes in with" value={str("enter")} options={ANIMATIONS_IN.map((a) => [a, a])} onChange={(v) => set("enter", v)} />
        <Select label="Goes out with" value={str("exit")} options={ANIMATIONS_OUT.map((a) => [a, a])} onChange={(v) => set("exit", v)} />
      </div>
      <div class="grid four">
        <MediaPicker label="Picture or video" kinds={["Image", "Video"]} uploads={uploads} value={str("media")} onChange={(v) => set("media", v)} />
        <Field label="Video volume %" type="number" value={str("mediaVolume")} onInput={(v) => set("mediaVolume", num(v, 50))} />
        <MediaPicker label="Sound" kinds={["Sound"]} uploads={uploads} value={str("sound")} onChange={(v) => set("sound", v)}
          extra={[["chime", "Chime (built in)"], ["none", "No sound"]]} />
        <Field label="Sound volume %" type="number" value={str("volume")} onInput={(v) => set("volume", num(v, 60))} />
      </div>
      <div class="grid four">
        <Field label="Font" value={str("font")} onInput={(v) => set("font", v)} />
        <Field label="Text size" type="number" value={str("size")} onInput={(v) => set("size", num(v, 42))} />
        <Field label="Colour" value={str("color")} onInput={(v) => set("color", v)} />
        <Field label="Highlight colour" value={str("highlight")} onInput={(v) => set("highlight", v)} />
      </div>
      <Check label="Read the alert out (text-to-speech)" checked={d.tts === true} onChange={(v) => set("tts", v)} />
      {d.tts === true && <Field label="What is read out" value={str("ttsText")} onInput={(v) => set("ttsText", v)} />}
    </div>
  );
}
