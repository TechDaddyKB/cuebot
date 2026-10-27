import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export const TEST_ROUTE_SECRET = "test-route-secret-0123456789abcdef";
export const TEST_CONNECT_TOKEN = "test-connect-token-0123456789abcdef";

export default defineConfig({
	plugins: [
		cloudflareTest({
			wrangler: { configPath: "./wrangler.jsonc" },
			miniflare: {
				// Test values only; the deployed Worker gets real ones from `wrangler secret put`.
				bindings: {
					ROUTE_SECRET: TEST_ROUTE_SECRET,
					CONNECT_TOKEN: TEST_CONNECT_TOKEN,
					MAX_BUFFERED: "3",
					MAX_BODY_BYTES: "1024",
				},
			},
		}),
	],
});
