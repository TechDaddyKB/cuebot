// Small form controls with labels, used by the editors.

let next = 0;
const uid = () => `f${++next}`;

export function Field(props: { label: string; value: string; onInput: (v: string) => void; multiline?: boolean; type?: string; help?: string; mono?: boolean }) {
  const id = uid();
  return (
    <div class="field">
      <label for={id}>{props.label}</label>
      {props.multiline
        ? <textarea id={id} rows={3} class={props.mono ? "mono" : ""} value={props.value} onInput={(e) => props.onInput((e.target as HTMLTextAreaElement).value)} />
        : <input id={id} type={props.type ?? "text"} value={props.value} onInput={(e) => props.onInput((e.target as HTMLInputElement).value)} />}
      {props.help && <small class="muted">{props.help}</small>}
    </div>
  );
}

export function Select(props: { label: string; value: string; options: [string, string][]; onChange: (v: string) => void; help?: string }) {
  const id = uid();
  const known = props.options.some(([v]) => v === props.value);
  return (
    <div class="field">
      <label for={id}>{props.label}</label>
      <select id={id} value={props.value} onChange={(e) => props.onChange((e.target as HTMLSelectElement).value)}>
        {!known && <option value={props.value}>{props.value}</option>}
        {props.options.map(([v, text]) => <option key={v} value={v}>{text}</option>)}
      </select>
      {props.help && <small class="muted">{props.help}</small>}
    </div>
  );
}

export function Check(props: { label: string; checked: boolean; onChange: (v: boolean) => void; help?: string }) {
  const id = uid();
  return (
    <div class="field check">
      <input id={id} type="checkbox" checked={props.checked} onChange={(e) => props.onChange((e.target as HTMLInputElement).checked)} />
      <label for={id}>{props.label}</label>
      {props.help && <small class="muted">{props.help}</small>}
    </div>
  );
}

/** Why a page's data could not be loaded (OBS not connected, not available in this Cuebot...). */
export function LoadError({ data }: { data: { ok: boolean; error?: string } | null }) {
  return data && !data.ok && data.error ? <p class="load-error">{data.error}</p> : null;
}
