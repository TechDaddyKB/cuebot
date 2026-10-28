/** Pure helpers for the property inspector (unit-tested without a DOM). */

/** Whether a section tagged data-kinds="run toggle" (and optionally data-controller="Encoder") shows for this key. */
export function sectionVisible(kinds: string, controller: string | undefined, kind: string | undefined, actual: string | undefined): boolean {
	const list = kinds.split(/\s+/).filter(Boolean);
	if (list.length > 0 && !list.includes("all") && (!kind || !list.includes(kind))) return false;
	if (controller && controller !== (actual ?? "Keypad")) return false;
	return true;
}

/** The connection indicator: a colour class and the text next to it. */
export function describeStatus(status: { state: string; message: string } | undefined): { className: string; text: string } {
	if (!status) return { className: "off", text: "Waiting for the plugin..." };
	switch (status.state) {
		case "connected":
			return { className: "ok", text: status.message || "Connected" };
		case "connecting":
			return { className: "busy", text: status.message || "Connecting..." };
		case "error":
			return { className: "bad", text: status.message || "Not connected" };
		default:
			return { className: "off", text: status.message || "Not connected" };
	}
}
