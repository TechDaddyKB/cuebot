import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

// The relay Worker's tests run inside workerd (test/worker). The crypto tests run in Node (vitest.config.ts).
export default defineConfig({
	plugins: [
		cloudflareTest({
			wrangler: { configPath: "./wrangler.jsonc" },
			// The built-in Kick app's client ID is a Worker secret in production; the Kick intake tests use this one.
			miniflare: { bindings: { KICK_CLIENT_ID: "kick-test-client" } },
		}),
	],
	test: { include: ["test/worker/**/*.test.ts"] },
});
