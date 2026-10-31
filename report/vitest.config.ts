import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
	plugins: [
		cloudflareTest({
			wrangler: { configPath: "./wrangler.jsonc" },
			miniflare: {
				// Test values only; the GitHub calls go to a fake in the tests and the key is generated there.
				bindings: {
					GITHUB_APP_ID: "12345",
					GITHUB_PRIVATE_KEY: "set by the tests",
					GITHUB_API: "https://github.test",
					PER_INSTALL_PER_HOUR: "3",
					PER_IP_PER_HOUR: "5",
				},
			},
		}),
	],
});
