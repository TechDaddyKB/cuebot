import { confirmRun, useLoad, usePortal, type Answer } from "../api.js";
import { LoadError } from "../widgets.js";

interface FeedItem { platform: string; kind: string; user: string; title: string; detail?: string; amount?: string; time: string }
interface Connection { name: string; state: string; message: string }
interface StreamRequest { id: string; title: string; detail: string; accept: string; remember: string; expires: number }
interface Dashboard extends Answer {
  feed?: FeedItem[];
  connections?: Connection[];
  quickActions?: { id: string; name: string; group: string; risk?: number }[];
  requests?: StreamRequest[];
}

export function HomePage() {
  const { me, call, notify } = usePortal();
  const [data, reload] = useLoad<Dashboard>("dashboard", 5000);
  const answer = async (id: string, how: "accept" | "always" | "decline") => {
    const result = await call("requests.answer", { id, answer: how });
    if (result.ok) notify(String((result as { message?: string }).message ?? "Done."));
    reload();
  };
  const run = async (id: string, name: string, risk?: number) => {
    if (!confirmRun(name, risk)) return;
    if ((await call("actions.run", { id })).ok) notify(`Started "${name}".`);
  };

  return (
    <>
      <h1>Hi {me.name}</h1>
      <LoadError data={data} />
      <p class="muted">
        You're a <strong>{me.role}</strong>.{" "}
        {me.changes === "NeedsApproval" ? "Your changes wait for the streamer's approval." : "Your changes go live straight away."}
      </p>

      {data?.requests && data.requests.length > 0 && (
        <section class="card attention">
          <h2>Needs a yes</h2>
          <ul class="rows">
            {data.requests.map((r) => (
              <li key={r.id} class="request">
                <div>
                  <strong>{r.title}</strong>
                  {r.detail && <div class="muted">{r.detail}</div>}
                </div>
                <div class="buttons">
                  <button type="button" class="primary" onClick={() => void answer(r.id, "accept")}>{r.accept || "Allow"}</button>
                  {r.remember && <button type="button" onClick={() => void answer(r.id, "always")}>{r.remember}</button>}
                  <button type="button" onClick={() => void answer(r.id, "decline")}>Ignore</button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      {data?.quickActions && (
        <section class="card">
          <h2>Quick actions</h2>
          {data.quickActions.length === 0 ? <p class="muted">No actions you can run yet.</p> : (
            <div class="grid buttons">
              {data.quickActions.map((a) => (
                <button type="button" key={a.id} onClick={() => void run(a.id, a.name, a.risk)} title={a.group || undefined}>{a.name}</button>
              ))}
            </div>
          )}
        </section>
      )}

      {data?.feed && (
        <section class="card">
          <h2>Activity</h2>
          {data.feed.length === 0 ? <p class="muted">Nothing yet this session.</p> : (
            <ul class="feed">
              {data.feed.map((f, i) => (
                <li key={i}>
                  <span class={`chip ${f.kind.toLowerCase()}`}>{f.platform}</span>
                  <span class="feed-text"><strong>{f.user}</strong> {f.title}{f.amount ? ` · ${f.amount}` : ""}</span>
                  <time class="muted">{new Date(f.time).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</time>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      {data?.connections && (
        <section class="card">
          <h2>Connections</h2>
          <ul class="rows">
            {data.connections.map((c) => (
              <li key={c.name}>
                <span class={`dot ${c.state.toLowerCase()}`} aria-hidden="true" />
                <strong>{c.name}</strong>
                <span class="muted">{c.message || c.state}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}
