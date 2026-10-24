// Production Understanding assets with an intercepted synthetic host. No real model or records.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
(async () => {
  const root = path.resolve(__dirname, '..');
  const output = path.resolve(process.argv[3]); fs.mkdirSync(output, { recursive: true });
  const assets = path.join(root, 'plugins/FgoPet.Plugin.Understanding/Desktop/ui');
  const browser = await chromium.launch(process.argv[4]
    ? { executablePath: path.resolve(process.argv[4]), headless: true }
    : { channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 900, height: 1000 }, reducedMotion: 'reduce' });
    let unexpected = 0;
    await page.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.hostname !== 'understanding.test') { unexpected++; return route.abort(); }
      if (url.pathname.endsWith('/understanding.js') || url.pathname.endsWith('/understanding.css')) {
        const name = path.basename(url.pathname);
        return route.fulfill({ contentType: name.endsWith('js') ? 'text/javascript' : 'text/css', body: fs.readFileSync(path.join(assets, name), 'utf8') });
      }
      return route.fulfill({ contentType: 'text/html', body: '<html lang="en"><meta charset="utf-8"><main></main></html>' });
    });
    await page.goto('https://understanding.test/');
    await page.evaluate(async () => {
      const entry = { id: 'interest', revision: 3, domain: 'self', kind: 'currentInterest', content: '<img src=x onerror="window.injected=true"> Synthetic interest',
        conditions: 'Synthetic project', review: 'approved', effect: 'approved', life: 'active', basisKind: 'assistant_stance',
        observedAt: '2026-10-22T00:00:00Z', activeUntil: '9999-12-31T23:59:59Z', sources: [] };
      const snapshot = window.fixture = { contextToken: 'fixture-context-1', scope: { servantId: 'synthetic', projectId: 'project' },
        presentation: { roleName: 'Synthetic companion', projectName: 'Synthetic project', modelRecipient: 'Offline synthetic model', automaticReviewAvailable: false },
        effectiveIds: [], state: { revision: 0, policy: { mode: 'off', enabled: true, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false } },
        accumulated: [entry], pending: [{ ...entry, id: 'pending-user', domain: 'relationship', content: 'Synthetic pending interpretation', review: 'unreviewed', effect: 'pending' }], changes: {} };
      window.sent = []; window.controller = new AbortController();
      const module = await import('/pages/understanding.js');
      window.dispose = await module.mount(document.querySelector('main'), { signal: window.controller.signal,
        async request(type, payload) {
          window.sent.push({ type, payload: structuredClone(payload) });
          if (window.failNext) { window.failNext = false; throw new Error('synthetic unavailable'); }
          if (type.startsWith('understanding.awareness.')) {
            const status = snapshot.awareness;
            if (!status || payload.contextToken !== status.contextToken) throw new Error('stale awareness context');
            const candidate = { token: 'synthetic-window-candidate', title: '<img src=x onerror="window.windowInjected=true"> Synthetic window', application: 'Synthetic app' };
            if (type === 'understanding.awareness.windows') return { contextToken: status.contextToken, windows: [candidate] };
            if (payload.revision !== status.revision) throw new Error('stale awareness revision');
            if (type === 'understanding.awareness.configure') { status.options = payload.options; status.modelApprovalCurrent = true; }
            else if (type === 'understanding.awareness.allowWindow' && payload.candidateToken === candidate.token) status.windows = [candidate];
            else if (type === 'understanding.awareness.clearWindows') status.windows = [];
            else throw new Error('unknown awareness command');
            status.revision++;
            return structuredClone(snapshot);
          }
          if (type !== 'understanding.get' && payload.contextToken !== snapshot.contextToken) throw new Error('stale context');
          if (type === 'understanding.configure') {
            snapshot.state.policy = payload.policy; snapshot.state.revision++;
            snapshot.contextToken = 'fixture-context-' + (snapshot.state.revision + 1);
          }
          if (type === 'understanding.review') {
            const item = [...snapshot.pending, ...snapshot.accumulated].find(e => e.id === payload.id && e.revision === payload.revision);
            if (!item) throw new Error('stale version');
            if (payload.action === 'correct') { item.correction = payload.correction; item.revision++; }
            if (payload.action === 'approve') {
              snapshot.pending = snapshot.pending.filter(e => e !== item);
              snapshot.accumulated.push({ ...item, revision: item.revision + 1, review: 'approved', effect: 'approved' });
            }
          }
          return structuredClone(snapshot);
        } });
    });
    assert.equal(await page.locator('article').count(), 1);
    assert.equal(await page.locator('article img').count(), 0);
    assert.equal(await page.evaluate(() => window.injected === true), false);
    assert.equal(await page.locator('select,input[type=checkbox]').count(), 0);
    await page.getByRole('button', { name: 'To review', exact: true }).click();
    await page.getByRole('button', { name: 'Correct', exact: true }).click();
    assert.equal(await page.locator('textarea').count(), 0);
    await page.locator('[data-local-review]').getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await page.evaluate(() => fixture.state.policy.readSources), false);
    await page.getByRole('button', { name: 'Correct', exact: true }).click();
    await page.getByRole('button', { name: 'Allow local review', exact: true }).click();
    await page.waitForFunction(() => fixture.state.policy.readSources);
    assert.equal(await page.evaluate(() => fixture.state.policy.useInDialogue), false);
    assert.equal(await page.evaluate(() => fixture.state.policy.externalProcessing), false);
    await page.getByRole('button', { name: 'Correct', exact: true }).click();
    await page.getByLabel('Your correction').fill('Synthetic corrected draft');
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    assert.equal(await page.getByLabel('Your correction').inputValue(), 'Synthetic corrected draft');
    await page.evaluate(() => { fixture.pending[0].revision++; });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Save correction', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Use current version', exact: true }).click();
    await page.getByRole('button', { name: 'Save correction', exact: true }).click();
    await page.waitForFunction(() => fixture.pending[0].correction === 'Synthetic corrected draft');
    assert.equal(await page.evaluate(() => fixture.pending[0].review), 'unreviewed');
    await page.getByRole('button', { name: 'Approve this version', exact: true }).click();
    await page.waitForFunction(() => fixture.pending.length === 0);
    const review = await page.evaluate(() => sent.find(r => r.type === 'understanding.review').payload);
    assert.equal(review.id, 'pending-user'); assert.equal(review.action, 'correct');
    assert.equal('roleId' in review, false); assert.equal(typeof review.contextToken, 'string');
    await page.getByRole('button', { name: 'Saved', exact: true }).click();
    assert.equal(await page.locator('article').count(), 2);
    await page.getByRole('button', { name: 'Forget', exact: true }).first().click();
    await page.locator('[data-forget]').getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await page.locator('[data-forget]').count(), 0);
    assert.equal(await page.evaluate(() => sent.some(r => r.payload.action === 'delete')), false);
    await page.setViewportSize({ width: 320, height: 800 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.evaluate(() => { window.failNext = true; });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    assert.equal(await page.locator('[data-error]').isVisible(), true);
    const understandingPolicy = await page.evaluate(() => structuredClone(fixture.state.policy));
    await page.evaluate(() => {
      fixture.awareness = { contextToken: 'awareness-context-1', revision: 0, available: true, modelRecipient: 'Offline ambient model', modelApprovalCurrent: false,
        options: { activityContextEnabled: true, applicationsEnabled: false, backgroundEnabled: false, dailyRequestLimit: 6 },
        requestsUsed: 0, windows: [], activityStatus: 'No background request made.' };
    });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    const awareness = page.locator('[data-awareness]');
    assert.equal(await awareness.isVisible(), true);
    assert.equal(await awareness.locator('input[type=checkbox]').count(), 3);
    assert.equal(await awareness.locator('[data-awareness-option=activityContextEnabled]').isChecked(), true);
    assert.equal(await awareness.locator('[data-awareness-option=applicationsEnabled]').isChecked(), false);
    assert.equal(await awareness.locator('[data-awareness-option=backgroundEnabled]').isChecked(), false);
    assert.equal(await page.evaluate(() => sent.some(r => r.type.startsWith('understanding.awareness.'))), false);
    assert.equal(await awareness.getByRole('button', { name: 'Allow a window', exact: true }).isDisabled(), true);
    await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.modelApprovalCurrent);
    assert.equal(await page.evaluate(() => fixture.awareness.options.backgroundEnabled), false);
    assert.deepEqual(await page.evaluate(() => fixture.state.policy), understandingPolicy);
    await awareness.locator('[data-awareness-option=applicationsEnabled]').check();
    await awareness.getByLabel('Model calls per rolling 24 hours').fill('25');
    assert.equal(await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).isDisabled(), true);
    await awareness.getByLabel('Model calls per rolling 24 hours').fill('6');
    assert.equal(await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).isEnabled(), true);
    await awareness.locator('[data-awareness-option=backgroundEnabled]').check();
    assert.equal(await page.evaluate(() => fixture.awareness.options.backgroundEnabled), false);
    await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.options.backgroundEnabled);
    assert.deepEqual(await page.evaluate(() => fixture.state.policy), understandingPolicy);
    await awareness.getByRole('button', { name: 'Allow a window', exact: true }).click();
    await awareness.getByRole('button', { name: 'Allow this window', exact: true }).waitFor();
    assert.equal(await awareness.locator('img').count(), 0);
    assert.equal(await page.evaluate(() => window.windowInjected === true), false);
    assert.equal(await page.evaluate(() => fixture.awareness.windows.length), 0);
    assert.equal(await page.evaluate(() => sent.filter(r => r.type === 'understanding.awareness.windows').length), 1);
    await awareness.getByRole('button', { name: 'Close window choices', exact: true }).click();
    assert.equal(await page.evaluate(() => sent.some(r => r.type === 'understanding.awareness.allowWindow')), false);
    await awareness.getByRole('button', { name: 'Allow a window', exact: true }).click();
    await awareness.getByRole('button', { name: 'Allow this window', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.windows.length === 1);
    assert.equal(await awareness.getByRole('button', { name: 'Allow this window', exact: true }).count(), 0);
    const allowWindow = await page.evaluate(() => sent.find(r => r.type === 'understanding.awareness.allowWindow').payload);
    assert.deepEqual(allowWindow, { contextToken: 'awareness-context-1', revision: 2, candidateToken: 'synthetic-window-candidate' });
    await awareness.getByRole('button', { name: 'Clear window access', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.windows.length === 0);
    assert.equal(await page.evaluate(() => sent.filter(r => r.type === 'understanding.awareness.windows').length), 2);
    await page.evaluate(() => { fixture.awareness.contextToken = 'awareness-context-2'; fixture.awareness.modelApprovalCurrent = false; fixture.awareness.modelRecipient = 'Another offline model'; });
    await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).click();
    assert.equal(await page.locator('[data-error]').isVisible(), true);
    assert.equal(await page.evaluate(() => fixture.awareness.modelApprovalCurrent), false);
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    assert.equal(await awareness.getByRole('button', { name: 'Allow a window', exact: true }).isDisabled(), true);
    assert.equal(await awareness.getByText('Model recipient: Another offline model.', { exact: true }).isVisible(), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: path.join(output, 'understanding-fixture.png'), fullPage: true });
    await page.evaluate(() => window.controller.abort());
    assert.equal(await page.locator('button:not(:disabled)').count(), 0); assert.equal(unexpected, 0);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ passed: true, setting: 'synthetic intercepted host; production assets', realProviderCalls: 0,
      checks: ['separate views', 'point-of-use consent and cancel', 'no implicit reply/external grant', 'correction stays pending', 'draft refresh and stale version', 'exact approve', 'forget cancel', 'inert text',
        'optional awareness host', 'no implicit background approval', 'bounded budget input', 'independent saved-understanding policy', 'explicit window enumeration', 'window choice cancel and revoke', 'inert window titles', 'stale model binding', 'narrow', 'failure', 'dispose'], unexpectedRequests: unexpected }, null, 2));
    process.stdout.write('Understanding browser fixture passed; real provider calls 0.\n');
  } finally { await browser.close(); }
})().catch(error => { process.stderr.write(String(error) + '\n'); process.exitCode = 1; });
