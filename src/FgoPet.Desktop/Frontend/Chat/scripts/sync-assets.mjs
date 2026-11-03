import { copyFile, mkdir, readFile, readdir, unlink } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const output = path.join(root, "dist");
const destination = path.resolve(root, "../../../../plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat");
const index = await readFile(path.join(output, "index.html"), "utf8");
if (!index.includes('name="margin-runtime" content="native"') || !index.includes("./assets/react-chat-"))
  throw new Error("Refusing to install a non-native chat entry.");

// Remove only generated resources referenced by the previous entry. Legacy
// compatibility scripts and unrelated user files are not build-owned outputs.
const previous = await readFile(path.join(destination, "index.html"), "utf8");
const oldAssets = [...previous.matchAll(/(?:src|href)="\.\/(assets\/[\w-]+\.(?:js|css))"/g)]
  .map(match => match[1]);
async function copyTree(relative = "") {
  for (const entry of await readdir(path.join(output, relative), { withFileTypes: true })) {
    const child = path.join(relative, entry.name);
    if (entry.isSymbolicLink()) throw new Error("Generated resources must be regular files.");
    if (entry.isDirectory()) {
      await mkdir(path.join(destination, child), { recursive: true });
      await copyTree(child);
    } else if (entry.isFile()) {
      await copyFile(path.join(output, child), path.join(destination, child));
    }
  }
}
await copyTree();
for (const relative of oldAssets) {
  if (!index.includes(`./${relative}`)) await unlink(path.join(destination, relative)).catch(error => {
    if (error.code !== "ENOENT") throw error;
  });
}
console.log("Installed the built native chat resources.");
