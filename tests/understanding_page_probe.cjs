// Synthetic browser regression for production settings assets. Every browser request is intercepted.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
(async () => {
  const root = path.resolve(__dirname, '..');
  const output = path.resolve(process.argv[3]); fs.mkdirSync(output, { recursive: true });
  const assets = path.join(root, 'plugins/FgoPet.Plugin.Understanding/Desktop/ui');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 1100 }, reducedMotion: 'reduce' });
    let unexpected = 0;
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.hostname !== 'understanding.test') { unexpected++; return route.abort(); }
      if (url.pathname.endsWith('/understanding.js') || url.pathname.endsWith('/understanding.css')) {
        const name = path.basename(url.pathname);
        return route.fulfill({ contentType: name.endsWith('js') ? 'text/javascript' : 'text/css', body: fs.readFileSync(path.join(assets, name), 'utf8') });
      }
      return route.fulfill({ contentType: 'text/html', body: '<html lang="zh-CN"><meta charset="utf-8"><main></main></html>' });
    });
    await page.goto('https://understanding.test/');
    await page.evaluate(async () => {
      const entry = { id: 'interest', revision: 3, domain: 'self', kind: 'currentInterest', content: '<img src=x onerror="window.injected=true"> synthetic interest',
        conditions: '仅此项目；暂定', review: 'unreviewed', effect: 'bounded', life: 'active', basisKind: 'assistant_stance',
        observedAt: '2026-10-18T00:00:00Z', activeUntil: '2026-10-25T00:00:00Z', sources: [] };
      const snapshot = { state: { revision: 2, policy: { mode: 'off', enabled: true, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false } },
        accumulated: [entry], pending: [{ ...entry, id: 'pending-user', domain: 'user', content: 'pending synthetic interpretation', effect: 'pending' }], changes: {} };
      window.sent = [];
      window.controller = new AbortController();
      const module = await import('/pages/understanding.js');
      window.dispose = await module.mount(document.querySelector('main'), { signal: window.controller.signal,
        async request(type, payload) {
          window.sent.push({ type, payload: structuredClone(payload) });
          if (type === 'understanding.configure') { snapshot.state.policy = payload.policy; snapshot.state.revision++; }
          if (type === 'understanding.review' && payload.action === 'approve') {
            const item = snapshot.pending.find(e => e.id === payload.id && e.revision === payload.revision);
            if (!item) throw new Error('stale');
            snapshot.pending = snapshot.pending.filter(e => e !== item); snapshot.accumulated.push({ ...item, revision: item.revision + 1, review: 'approved', effect: 'approved' });
          }
          return structuredClone(snapshot);
        } });
    });
    assert.equal(await page.locator('article').count(), 1);
    assert.equal(await page.locator('article img').count(), 0);
    assert.equal(await page.evaluate(() => window.injected === true), false);
    await page.getByRole('button', { name: '待批准', exact: true }).click();
    assert.equal(await page.locator('article').count(), 1);
    await page.getByRole('button', { name: '批准此版本', exact: true }).click();
    await page.waitForFunction(() => window.sent.some(r => r.type === 'understanding.review'));
    const review = await page.evaluate(() => window.sent.find(r => r.type === 'understanding.review').payload);
    assert.equal(review.id, 'pending-user'); assert.equal(review.revision, 3); assert.equal(review.action, 'approve');
    assert.equal('roleId' in review, false);
    await page.getByRole('button', { name: '已积累', exact: true }).click();
    assert.equal(await page.locator('article').count(), 2);
    await page.locator('select[name="mode"]').selectOption('proposals');
    await page.locator('input[name="readSources"]').check();
    await page.getByRole('button', { name: '保存权限与模式', exact: true }).click();
    await page.waitForFunction(() => window.sent.some(r => r.type === 'understanding.configure'));
    const configured = await page.evaluate(() => window.sent.find(r => r.type === 'understanding.configure').payload);
    assert.equal(configured.policy.mode, 'proposals'); assert.equal(configured.policy.externalProcessing, false);
    assert.equal(configured.policy.useInDialogue, false);
    await page.screenshot({ path: path.join(output, 'understanding-fixture.png'), fullPage: true });
    await page.evaluate(() => window.controller.abort());
    assert.equal(await page.locator('button:not(:disabled)').count(), 0);
    assert.equal(unexpected, 0);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ passed: true, setting: 'synthetic host; production assets; intercepted browser network', realProviderCalls: 0,
      checks: ['separate views', 'exact revision approve', 'independent grants', 'textContent injection boundary', 'dispose', 'reduced motion'], unexpectedRequests: unexpected }, null, 2));
    process.stdout.write('Understanding browser fixture passed; real provider calls 0.\n');
  } finally { await browser.close(); }
})().catch(error => { process.stderr.write(String(error) + '\n'); process.exitCode = 1; });
