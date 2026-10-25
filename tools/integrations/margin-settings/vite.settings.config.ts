import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const root = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  base: "./",
  publicDir: false,
  plugins: [{
    name: "margin-settings-font-licenses",
    async generateBundle(_options, bundle) {
      for (const item of Object.values(bundle)) {
        if (item.type === "chunk" && item.isEntry && !item.exports.includes("mount"))
          this.error("Settings entry must export mount: " + item.fileName);
      }
      for (const name of ["OFL-DM-Sans.txt", "OFL-DM-Serif-Display.txt"]) {
        this.emitFile({
          type: "asset",
          fileName: "margin/fonts/" + name,
          source: await readFile(path.join(root, "public", "fonts", name)),
        });
      }
    },
  }],
  build: {
    outDir: "dist/settings",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    cssCodeSplit: false,
    modulePreload: false,
    rollupOptions: {
      input: {
        theme: path.join(root, "src/settings-native/theme.tsx"),
        "model-connection": path.join(root, "src/settings-native/model-connection.tsx"),
      },
      // Settings root dynamically imports these modules and calls their mount export.
      preserveEntrySignatures: "strict",
      output: {
        format: "es",
        entryFileNames: "pages/[name].js",
        chunkFileNames: "margin/assets/[name]-[hash].js",
        assetFileNames(asset) {
          const name = asset.names[0] || asset.name || "";
          if (name.endsWith(".css")) return "margin/margin-settings.css";
          if (/\.woff2?$/i.test(name)) return "margin/fonts/[name]-[hash][extname]";
          return "margin/assets/[name]-[hash][extname]";
        },
      },
    },
  },
});
