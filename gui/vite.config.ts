import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The GUI is a separate-origin SPA: it talks to the control plane cross-origin via CORS, so no dev proxy is
// needed; the API base URL comes from public/config.json at runtime (VITE_API_BASE_URL as the build-time
// fallback). Port 5173 matches the origin the control plane's development CORS allowlist grants.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    outDir: "dist",
    sourcemap: true,
    chunkSizeWarningLimit: 4096, // monaco is deliberately bundled (no CDN), and it is large
  },
});
