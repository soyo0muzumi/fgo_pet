const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const handlers = {}, calls = [], errors = [];
let identity = 'one', busy = false;
const context = { crypto: { randomUUID }, btoa: text => Buffer.from(text, 'binary').toString('base64'), Uint8Array };
vm.createContext(context);
vm.runInContext(readFileSync(resolve(__dirname, '../plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat/image-import.js'), 'utf8'), context);
context.ChatImageImport.install({ addEventListener: (type, fn) => handlers[type] = fn }, {
  canImport: () => true, identity: () => identity, busy: v => busy = v, error: e => errors.push(e),
  send: async (type, data) => calls.push({type, ...data})
});
function event(files, trusted = true) { return { isTrusted: trusted, clipboardData: { files }, prevented: false, preventDefault() { this.prevented = true; } }; }
function file(size = 50000, type = 'image/png') { return {size, type, slice: (start, end) => ({arrayBuffer: async () => new Uint8Array(Math.min(size, end) - start).buffer})}; }
(async () => {
  const text = event([]); await handlers.paste(text); assert.equal(text.prevented, false);
  await handlers.paste(event([file()], false)); assert.equal(calls.length, 0);
  const paste = event([file()]); await handlers.paste(paste);
  assert(paste.prevented); assert.equal(busy, false); assert.equal(calls.length, 3);
  assert.deepEqual(calls.map(c=>c.offset), [0,24576,49152]); assert(calls[2].complete);
  assert(calls.every(c=>c.type==='chat.image' && c.data.length<=49152));
  await handlers.drop(event([file(),file()])); assert.equal(errors.at(-1),'invalid');
  await handlers.paste(event([file(8388609)])); assert.equal(errors.at(-1),'invalid');
  await handlers.paste(event([file(1,'image/svg+xml')])); assert.equal(calls.length,3);
  const stale = file(); stale.slice = () => ({arrayBuffer: async () => {identity='two'; return new Uint8Array(1).buffer;}});
  await handlers.paste(event([stale])); assert.equal(calls.length,3); assert.equal(busy,false);
  console.log('image import: text preserved, trusted gesture, ordered chunks, limits, stale-session cancellation PASS');
})().catch(e=>{console.error(e);process.exitCode=1;});
