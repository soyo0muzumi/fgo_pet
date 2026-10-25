// Copy only this build's two modules and referenced static resources. No native
// installation, package restore, Git operation, application state or cleanup.
import { copyFile, mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const build = path.join(here, 'dist/settings');
const root = path.join(repo, 'src/FgoPet.Desktop/Shell/Desktop/ui/settings/root');
const targets = {
  'pages/theme.js': 'plugins/FgoPet.Plugin.Content/Desktop/Desktop/ui/settings/theme/theme.js',
  'pages/model-connection.js': 'plugins/FgoPet.Plugin.Dialogue/Desktop/ui/settings/model-connection/model-connection.js',
};
const manifest = [];
async function copy(relative) {
  const source = path.join(build, relative);
  const destination = targets[relative] ? path.join(repo, targets[relative]) : path.join(root, relative);
  if (!destination.startsWith(repo + path.sep)) throw new Error('Invalid destination');
  await mkdir(path.dirname(destination), { recursive: true });
  await copyFile(source, destination);
  manifest.push({ path: relative.replaceAll('\\', '/'), sha256: createHash('sha256').update(await readFile(source)).digest('hex') });
}
async function walk(relative) {
  for (const entry of await readdir(path.join(build, relative), { withFileTypes: true })) {
    const child = path.join(relative, entry.name);
    if (entry.isDirectory()) await walk(child);
    else if (entry.isFile()) await copy(child);
    else throw new Error('Unexpected build entry');
  }
}
for (const entry of Object.keys(targets)) {
  if (!/\bas\s+mount\b|\bfunction\s+mount\b/.test(await readFile(path.join(build, entry), 'utf8'))) throw new Error('Missing mount export');
  await copy(entry);
}
await walk('margin');
await copyFile(path.join(here, 'THIRD-PARTY-NOTICES.txt'), path.join(root, 'margin/THIRD-PARTY-NOTICES.txt'));
await writeFile(path.join(root, 'margin/build-manifest.json'), JSON.stringify({ source: 'tools/integrations/margin-settings', files: manifest }, null, 2) + '\n');
console.log(`Synced ${manifest.length} Settings resources; existing unrelated assets preserved.`);
