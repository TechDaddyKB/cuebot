import { useState } from "preact/hooks";
import { useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface Speech extends Answer { enabled?: boolean; paused?: boolean; queue?: number; current?: string | null; canControl?: boolean }

const MAX = 300;

export function SpeechPage() {
  const { call, can, notify } = usePortal();
  const [data, reload] = useLoad<Speech>("tts.state", 3000);
  const [text, setText] = useState("");
  const control = async (command: string) => {
    await call("tts.control", { command });
    reload();
  };
  const say = async (e: Event) => {
    e.preventDefault();
    if ((await call("tts.speak", { text })).ok) {
      setText("");
      notify("Queued to be spoken.");
      reload();
    }
  };
  return (
    <>
      <h1>Text-to-speech</h1>
      <LoadError data={data} />
      {data?.ok && (
        <>
          <p class="muted">
            {!data.enabled ? "Text-to-speech is off." : data.paused ? "Paused." : data.current ? `Speaking: “${data.current}”` : "Nothing is being spoken."}
            {data.queue ? ` ${data.queue} waiting.` : ""}
          </p>
          {can("RunActions") && (
            <form class="card" onSubmit={say}>
              <label for="tts-text">Say something on stream</label>
              <textarea id="tts-text" rows={3} maxLength={MAX} value={text} disabled={!data.enabled}
                onInput={(e) => setText((e.target as HTMLTextAreaElement).value)} />
              <div class="form-row">
                <span class="muted">{text.length}/{MAX} · the streamer's default voice and word filters</span>
                <button type="submit" class="primary" disabled={!data.enabled || text.trim().length === 0}>Say it</button>
              </div>
            </form>
          )}
          {data.canControl && (
            <section class="card">
              <h2>Controls</h2>
              <div class="row-buttons">
                {data.paused
                  ? <button type="button" onClick={() => void control("resume")}>Resume</button>
                  : <button type="button" onClick={() => void control("pause")}>Pause</button>}
                <button type="button" onClick={() => void control("skip")}>Skip current</button>
                <button type="button" disabled={!data.queue} onClick={() => void control("clear")}>Clear waiting</button>
                {data.enabled
                  ? <button type="button" class="danger" onClick={() => void control("disable")}>Turn off</button>
                  : <button type="button" onClick={() => void control("enable")}>Turn on</button>}
              </div>
            </section>
          )}
        </>
      )}
    </>
  );
}
