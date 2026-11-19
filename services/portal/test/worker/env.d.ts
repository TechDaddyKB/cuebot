import type { Env as RelayEnv } from "../../worker/index";

declare global {
	namespace Cloudflare {
		interface Env extends RelayEnv {}
	}
}
