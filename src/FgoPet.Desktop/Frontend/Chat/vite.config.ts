import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  cacheDir: ".vite",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        entryFileNames: "assets/react-chat-[hash].js",
        assetFileNames: "assets/react-chat-[hash][extname]",
      },
    },
  },
});
