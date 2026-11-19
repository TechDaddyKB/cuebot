import type { Env as IntakeEnv } from "../src/index";

declare global {
	namespace Cloudflare {
		interface Env extends IntakeEnv {}
	}
}
