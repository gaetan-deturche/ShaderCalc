import { defineConfig } from "vite";

// In a plain browser the UI talks to the dev server (cargo run -p shadercalc-devserver) through /api
export default defineConfig({
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    proxy: { "/api": "http://127.0.0.1:5191" },
  },
  build: {
    target: "es2022",
    outDir: "dist",
  },
});
