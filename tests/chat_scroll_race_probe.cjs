// Actual production assets in installed Edge. Controlled frame order, synthetic data, no provider calls.
// node tests/chat_scroll_race_probe.cjs <playwright-module> <ignored-output-directory>
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
const root = path.resolve(__dirname, '../plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat');
const output = path.resolve(process.argv[3]);
fs.mkdirSync(output, { recursive: true });
const record = (stage, result, fields = {}) => {
  const event = { atUtc: new Date().toISOString(), correlation: 'synthetic-scroll-frame', stage, result, ...fields };
  fs.appendFileSync(path.join(output, 'events.jsonl'), JSON.stringify(event) + '\n');
  console.log(JSON.stringify(event));
};
const server = http.createServer((request, response) => {
  const name = new URL(request.url, 'http://localhost').pathname.slice(1) || 'index.html';
  const base = ['margin.css', 'margin-select.js'].includes(name)
    ? path.resolve(root, '../../../../../src/FgoPet.UiSdk/Resources/Web') : root;
  const file = path.resolve(base, name);
  if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end(); return;
  }
  response.writeHead(200, { 'Content-Type': { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript' }[path.extname(file)] || 'application/octet-stream' });
  response.end(fs.readFileSync(file));
});
(async () => {
  let browser;
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  try {
    record('session_start', 'started', { realProviderCalls: 0 });
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 376, height: 500 }, reducedMotion: 'reduce' });
    await page.route('**/*', route => route.request().url().startsWith(origin + '/') ? route.continue() : route.abort());
    await page.addInitScript(() => {
      const fixture = {
        version: 1, identity: { sessionId: 'synthetic', servantId: 'synthetic', conversationId: 'synthetic' },
        draft: { text: '', revision: 0 }, presentation: { roleName: 'Synthetic companion' },
        conversation: { canSend: true, isStreaming: false, canStop: false,
          turns: Array.from({ length: 20 }, (_, i) => ({ id: `synthetic-${i}`, role: 'assistant', text: ('Synthetic reading line.\n').repeat(20) })) },
        history: { items: [] }
      };
      let receive;
      window.chrome ??= {};
      window.chrome.webview = {
        addEventListener(type, callback) { receive = callback; },
        postMessage(message) {
          if (!message.requestId) return;
          fixture.version++;
          setTimeout(() => receive({ data: { type: 'command.result', requestId: message.requestId, success: true,
            payload: { snapshot: structuredClone(fixture) } } }), 0);
        }
      };
      const nativeRaf = window.requestAnimationFrame.bind(window);
      const nativeCancel = window.cancelAnimationFrame.bind(window);
      let next = 1000000;
      window.heldFrames = null;
      window.requestAnimationFrame = callback => {
        if (!window.heldFrames) return nativeRaf(callback);
        const id = next++; window.heldFrames.set(id, callback); return id;
      };
      window.cancelAnimationFrame = id => window.heldFrames?.delete(id) || nativeCancel(id);
      window.drainFrames = () => {
        let total = 0;
        for (let round = 0; round < 8 && window.heldFrames.size; round++) {
          const batch = [...window.heldFrames.values()]; window.heldFrames.clear();
          for (const callback of batch) { callback(performance.now()); total++; }
        }
        return total;
      };
    });
    await page.goto(origin + '/index.html');
    await page.waitForFunction(() => !document.getElementById('composer').disabled && document.querySelectorAll('#messages article').length === 20);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const race = await page.evaluate(() => {
      const scroll = document.getElementById('message-scroll');
      scroll.scrollTo({ top: scroll.scrollHeight, behavior: 'instant' }); scroll.dispatchEvent(new Event('scroll'));
      return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
        window.heldFrames = new Map();
        window.dispatchEvent(new Event('resize')); // A previously queued layout frame.
        scroll.scrollTo({ top: 500, behavior: 'instant' }); scroll.dispatchEvent(new Event('scroll'));
        const queued = window.heldFrames.size;
        const drained = window.drainFrames();
        const result = { top: Math.round(scroll.scrollTop), queued, drained };
        window.heldFrames = null;
        resolve(result);
      })));
    });
    record('resize_before_scroll_frame', race.top === 500 ? 'passed' : 'failed', race);
    assert.ok(race.queued >= 2 && race.drained >= 2, 'Exercise both deferred layout and scroll frames');
    assert.equal(race.top, 500, 'A queued resize must preserve the newer reading position');
    const following = await page.evaluate(() => {
      const scroll = document.getElementById('message-scroll');
      window.heldFrames = new Map();
      scroll.scrollTo({ top: scroll.scrollHeight, behavior: 'instant' }); scroll.dispatchEvent(new Event('scroll'));
      window.dispatchEvent(new Event('resize')); window.drainFrames(); window.heldFrames = null;
      return { gap: scroll.scrollHeight - scroll.clientHeight - scroll.scrollTop, jumpHidden: document.getElementById('jump-bottom').hidden };
    });
    assert.ok(following.gap <= 2);
    assert.equal(following.jumpHidden, true);
    record('explicit_latest_still_follows', 'passed', following);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const animation = await page.evaluate(() => {
      const scroll = document.getElementById('message-scroll');
      const turns = [...document.querySelectorAll('#messages article')];
      window.heldFrames = new Map();
      turns.forEach(node => { node.style.transform = 'translateY(5px)'; });
      scroll.scrollTo({ top: 500, behavior: 'instant' }); scroll.dispatchEvent(new Event('scroll'));
      window.drainFrames();
      turns.forEach(node => { node.style.transform = ''; });
      window.dispatchEvent(new Event('resize'));
      const queued = window.heldFrames.size;
      const drained = window.drainFrames(); window.heldFrames = null;
      return { top: Math.round(scroll.scrollTop), queued, drained };
    });
    record('entry_animation_does_not_change_anchor', animation.top === 500 ? 'passed' : 'failed', animation);
    assert.ok(animation.queued >= 1 && animation.drained >= 1, 'Exercise the layout restoration after animation');
    assert.equal(animation.top, 500, 'Entry animation is visual motion, not a change to the reading anchor');
    record('session_complete', 'passed', { realProviderCalls: 0 });
  } catch (error) {
    record('session_complete', 'failed', { code: 'SCROLL_FRAME_REGRESSION', realProviderCalls: 0 });
    throw error;
  } finally { await browser?.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
