import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

// The relay Worker's tests run inside workerd (test/worker). The crypto tests run in Node (vitest.config.ts).
export default defineConfig({
	plugins: [cloudflareTest({ wrangler: { configPath: "./wrangler.jsonc" } })],
	test: { include: ["test/worker/**/*.test.ts"] },
});
