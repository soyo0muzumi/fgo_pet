// Production Understanding assets with an intercepted synthetic host. No real model or records.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
(async () => {
  const root = path.resolve(__dirname, '..');
  const output = path.resolve(process.argv[3]); fs.mkdirSync(output, { recursive: true });
  const assets = path.join(root, 'plugins/FgoPet.Plugin.Understanding/Desktop/ui');
  const shellCss = ['src/FgoPet.UiSdk/Resources/Web/margin.css',
    'src/FgoPet.Desktop/Shell/Desktop/ui/settings/root/root.css',
    'src/FgoPet.Desktop/Shell/Desktop/ui/settings/root/settings-components.css']
    .map(file => fs.readFileSync(path.join(root, file), 'utf8')).join('\n');
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
      return route.fulfill({ contentType: 'text/html', body: `<html lang="en"><meta charset="utf-8"><style>${shellCss}</style>
        <div class="settings-main" style="height:100%"><div class="settings-location">Memory & understanding / Understanding</div>
        <div class="settings-scroll"><header class="page-heading"><h1>Understanding</h1></header><main class="page-content"></main></div></div></html>` });
    });
    await page.goto('https://understanding.test/');
    await page.evaluate(async () => {
      const entry = { id: 'interest', revision: 3, domain: 'self', kind: 'currentInterest', content: '<img src=x onerror="window.injected=true"> Synthetic interest',
        conditions: 'Synthetic project', review: 'approved', effect: 'approved', life: 'active', basisKind: 'assistant_stance',
        observedAt: '2026-10-22T00:00:00Z', activeUntil: '9999-12-31T23:59:59Z',
        sources: [{ kind: 'user', occurredAtUtc: '2026-10-22T00:00:00Z', messageId: 'synthetic-internal-message-id', fingerprint: 'synthetic-internal-fingerprint' }],
        coverage: [{ complete: false, includedScalars: 12345, totalScalars: 23456, startScalar: 34567 }] };
      const snapshot = window.fixture = { contextToken: 'fixture-context-1', scope: { servantId: 'synthetic', projectId: 'project' },
        presentation: { roleName: 'Synthetic companion', projectName: 'Synthetic project', modelRecipient: 'Offline synthetic model', automaticReviewAvailable: false },
        effectiveIds: [], state: { revision: 0, policy: { mode: 'off', enabled: true, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false } },
        accumulated: [entry], pending: [{ ...entry, id: 'pending-user', domain: 'relationship', content: 'Synthetic pending interpretation', review: 'unreviewed', effect: 'pending' }], changes: {} };
      window.sent = []; window.controller = new AbortController();
      const module = await import('/pages/understanding.js');
      window.fixtureContext = { signal: window.controller.signal,
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
          if (type === 'understanding.configure' || type === 'understanding.automation') {
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
        } };
      window.dispose = await module.mount(document.querySelector('main'), window.fixtureContext);
      window.remount = async () => {
        const state = structuredClone(window.dispose.getState());
        window.controller.abort();
        window.controller = new AbortController();
        window.fixtureContext = { ...window.fixtureContext, state, signal: window.controller.signal };
        window.dispose = await module.mount(document.querySelector('main'), window.fixtureContext);
      };
    });
    assert.equal(await page.locator('article').count(), 1);
    await page.screenshot({ path: path.join(output, 'understanding-saved.png'), fullPage: true });
    assert.equal(await page.locator('article img').count(), 0);
    assert.equal(await page.evaluate(() => window.injected === true), false);
    const settings = page.locator('[data-settings]');
    const awarenessSettings = page.locator('[data-awareness-settings]');
    const searchTools = page.locator('[data-search-tools]');
    const setDisclosure = async (element, open) => {
      if (await element.evaluate(el => el.open) !== open) await element.locator(':scope > summary').click();
    };
    assert.equal(await page.getByRole('tab').count(), 0);
    assert.equal(await settings.evaluate(el => el.open), false);
    assert.equal(await searchTools.evaluate(el => el.open), false);
    assert.equal(await page.locator('select:not([data-domain]),input[type=checkbox]').count(), 0);
    assert.equal(await page.getByLabel('Filter by topic', { exact: true }).count(), 1);
    const search = page.getByLabel('Search understanding', { exact: true });
    const topic = page.getByLabel('Filter by topic', { exact: true });
    const allTopics = await topic.locator('option').first().getAttribute('value');
    const initialCalls = await page.evaluate(() => sent.length);
    await setDisclosure(searchTools, true);
    await search.fill('Synthetic interest');
    assert.equal(await page.locator('article').count(), 1);
    await search.fill('no matching synthetic interpretation');
    assert.equal(await page.locator('article').count(), 0);
    assert.equal(await page.locator('[data-entries] .understanding-empty').isVisible(), true);
    await search.fill('');
    await topic.selectOption('relationship');
    assert.equal(await page.locator('article').count(), 0);
    await topic.selectOption('self');
    assert.equal(await page.locator('article').count(), 1);
    await topic.selectOption(allTopics);
    await setDisclosure(settings, true);
    assert.equal(await page.locator('[data-service-availability],[data-access]').count(), 0);
    assert.equal(await settings.locator('summary').filter({ hasText: /^Review & permissions$/ }).count(), 0);
    assert.equal(await page.locator('article').isVisible(), true);
    await setDisclosure(settings, false);
    assert.equal(await searchTools.locator('[data-archive]').count(), 1);
    await searchTools.locator('[data-archive]').click();
    assert.equal(await page.getByRole('button', { name: 'Archived', exact: true }).getAttribute('aria-pressed'), 'true');
    await page.getByRole('button', { name: 'Saved', exact: true }).click();
    assert.equal(await page.evaluate(() => sent.length), initialCalls);
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
    const draftCalls = await page.evaluate(() => sent.length);
    await setDisclosure(settings, true);
    await setDisclosure(settings, false);
    assert.equal(await page.getByLabel('Your correction').inputValue(), 'Synthetic corrected draft');
    await page.getByRole('button', { name: 'Saved', exact: true }).click();
    await page.getByRole('button', { name: 'To review', exact: true }).click();
    assert.equal(await page.getByLabel('Your correction').inputValue(), 'Synthetic corrected draft');
    assert.equal(await page.evaluate(() => sent.length), draftCalls);
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
    await page.locator('article').first().locator('summary').filter({ hasText: /^Details$/ }).click();
    const detailsText = await page.locator('[data-entries]').textContent();
    assert.doesNotMatch(detailsText, /Technical details|synthetic-internal-message-id|synthetic-internal-fingerprint|Version \d+|12345|23456|34567/);
    assert.match(detailsText, /Your message/);
    await page.getByRole('button', { name: 'Forget', exact: true }).first().click();
    await page.locator('[data-forget]').getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await page.locator('[data-forget]').count(), 0);
    assert.equal(await page.evaluate(() => sent.some(r => r.payload.action === 'delete')), false);
    await page.setViewportSize({ width: 320, height: 800 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.evaluate(() => { window.failNext = true; });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    assert.equal(await page.locator('[data-error]').isVisible(), true);
    await setDisclosure(settings, true);
    await page.evaluate(() => {
      fixture.presentation.automaticReviewAvailable = true;
      fixture.automation = {routeHash:'a'.repeat(64),budget:{limit:12,used:0,resetsAt:'2026-10-25T00:00:00Z'},code:'UNDERSTANDING_BUDGET_UNAUTHORIZED'};
    });
    await page.getByRole('button', {name:'Refresh',exact:true}).click();
    await page.getByRole('button', {name:'开启自动理解',exact:true}).click();
    assert.equal(await page.getByLabel('生效方式').inputValue(), 'bounded');
    assert.equal(await page.getByRole('button',{name:'确认自动理解设置'}).isDisabled(),true);
    await page.getByLabel('允许将所选对话片段发送给上述模型分析').check();
    await page.getByLabel('允许保存带来源的理解与待确认候选').check();
    assert.equal(await page.getByLabel('允许在后续回复中使用有效理解（待确认内容除外）').isChecked(),false);
    await page.getByLabel('每日后台派发上限').fill('18');
    await page.getByRole('button',{name:'确认自动理解设置'}).click();
    await page.waitForFunction(()=>fixture.state.policy.mode==='bounded');
    const auto = await page.evaluate(()=>sent.find(r=>r.type==='understanding.automation').payload);
    assert.equal(auto.dailyLimit,18); assert.equal(auto.policy.useInDialogue,false); assert.equal(auto.routeHash,'a'.repeat(64));
    await page.evaluate(() => {
      fixture.automation.code = null;
      fixture.lastRun = { stage: 'failed', code: 'SYNTHETIC_REVIEW_FAILED', at: '2026-10-25T00:00:00Z', newItems: 0 };
    });
    await page.getByRole('button', {name:'Refresh',exact:true}).click();
    assert.match(await page.locator('[data-review-summary]').textContent(), /last review failed/);
    assert.equal(await page.locator('[data-review-diagnostics]').isVisible(), false);
    await page.locator('[data-review-details] > summary').click();
    assert.match(await page.locator('[data-review-diagnostics]').textContent(), /SYNTHETIC_REVIEW_FAILED/);
    await page.evaluate(() => { fixture.lastRun.stage = 'completed'; });
    await page.getByRole('button', {name:'Refresh',exact:true}).click();
    assert.equal(await page.locator('[data-review-health]').isVisible(), false);
    await setDisclosure(settings, false);
    const understandingPolicy = await page.evaluate(() => structuredClone(fixture.state.policy));
    await page.evaluate(() => {
      fixture.awareness = { contextToken: 'awareness-context-1', revision: 0, available: true, modelRecipient: 'Offline ambient model', modelApprovalCurrent: false,
        options: { activityContextEnabled: true, applicationsEnabled: false, backgroundEnabled: false, dailyRequestLimit: 13 },
        requestsUsed: 0, windows: [], activityStatus: 'No background request made.' };
    });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    const awareness = page.locator('[data-awareness]');
    assert.equal(await awareness.isVisible(), false);
    const navigationCalls = await page.evaluate(() => sent.length);
    await settings.locator(':scope > summary').focus();
    await page.keyboard.press('Enter');
    assert.equal(await settings.evaluate(el => el.open), true);
    assert.equal(await awareness.isVisible(), false);
    await awarenessSettings.locator(':scope > summary').focus();
    await page.keyboard.press('Enter');
    assert.equal(await awarenessSettings.evaluate(el => el.open), true);
    assert.equal(await awareness.isVisible(), true);
    await page.keyboard.press('Space');
    assert.equal(await awarenessSettings.evaluate(el => el.open), false);
    assert.equal(await awareness.isVisible(), false);
    await page.keyboard.press('Space');
    assert.equal(await awarenessSettings.locator(':scope > summary').evaluate(el => document.activeElement === el), true);
    assert.equal(await page.evaluate(() => sent.length), navigationCalls);
    assert.equal(await awareness.isVisible(), true);
    assert.equal(await awareness.locator('input[type=checkbox]').count(), 3);
    assert.equal(await awareness.locator('[data-awareness-option=activityContextEnabled]').isChecked(), true);
    assert.equal(await awareness.locator('[data-awareness-option=applicationsEnabled]').isChecked(), false);
    assert.equal(await awareness.locator('[data-awareness-option=backgroundEnabled]').isChecked(), false);
    assert.equal(await awareness.locator('input[type=number]').count(), 0);
    assert.equal(await awareness.locator('summary').filter({ hasText: /^Request limits$/ }).count(), 0);
    assert.match(await awareness.locator('[data-awareness-budget]').textContent(), /13/);
    assert.doesNotMatch(await awareness.textContent(), /4,096|4096|384|tokens|Requests in the last 24 hours/);
    assert.equal(await page.evaluate(() => sent.some(r => r.type.startsWith('understanding.awareness.'))), false);
    assert.equal(await awareness.getByRole('button', { name: 'Allow a window', exact: true }).isDisabled(), true);
    await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.modelApprovalCurrent);
    assert.equal(await page.evaluate(() => fixture.awareness.options.backgroundEnabled), false);
    assert.equal(await page.evaluate(() => sent.find(r => r.type === 'understanding.awareness.configure').payload.options.dailyRequestLimit), 13);
    assert.deepEqual(await page.evaluate(() => fixture.state.policy), understandingPolicy);
    await awareness.locator('[data-awareness-option=applicationsEnabled]').check();
    const awarenessDraftCalls = await page.evaluate(() => sent.length);
    await setDisclosure(settings, false);
    await setDisclosure(settings, true);
    assert.equal(await awareness.locator('[data-awareness-option=applicationsEnabled]').isChecked(), true);
    assert.equal(await page.evaluate(() => fixture.awareness.options.applicationsEnabled), false);
    assert.equal(await page.evaluate(() => sent.length), awarenessDraftCalls);
    await setDisclosure(settings, false);
    await setDisclosure(searchTools, true);
    await search.fill('Synthetic pending');
    await topic.selectOption('relationship');
    await page.getByRole('button', { name: 'To review', exact: true }).click();
    await setDisclosure(settings, true);
    const beforeRemount = await page.evaluate(() => sent.length);
    await page.evaluate(async () => window.remount());
    assert.equal(await settings.evaluate(el => el.open), true);
    assert.equal(await awarenessSettings.evaluate(el => el.open), true);
    assert.equal(await searchTools.evaluate(el => el.open), true);
    assert.equal(await awareness.locator('[data-awareness-option=applicationsEnabled]').isChecked(), true);
    assert.equal(await page.evaluate(() => fixture.awareness.options.applicationsEnabled), false);
    assert.match(await awareness.locator('[data-awareness-budget]').textContent(), /13/);
    assert.deepEqual(await page.evaluate(index => sent.slice(index).map(r => r.type), beforeRemount), ['understanding.get']);
    await setDisclosure(settings, false);
    assert.equal(await search.inputValue(), 'Synthetic pending');
    assert.equal(await topic.inputValue(), 'relationship');
    assert.equal(await page.getByRole('button', { name: 'To review', exact: true }).getAttribute('aria-pressed'), 'true');
    await search.fill('');
    await topic.selectOption(allTopics);
    await page.getByRole('button', { name: 'Saved', exact: true }).click();
    await setDisclosure(settings, true);
    assert.equal(await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).isEnabled(), true);
    await awareness.locator('[data-awareness-option=backgroundEnabled]').check();
    assert.equal(await page.evaluate(() => fixture.awareness.options.backgroundEnabled), false);
    await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).click();
    await page.waitForFunction(() => fixture.awareness.options.backgroundEnabled);
    assert.equal(await page.evaluate(() => fixture.awareness.options.applicationsEnabled), true);
    assert.equal(await page.evaluate(() => fixture.awareness.options.dailyRequestLimit), 13);
    assert.deepEqual(await page.evaluate(() => sent.filter(r => r.type === 'understanding.awareness.configure').map(r => r.payload.options.dailyRequestLimit)), [13, 13]);
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
    for (const invalidBudget of [25, null]) {
      await page.evaluate(value => {
        fixture.awareness.revision++;
        fixture.awareness.options.dailyRequestLimit = value;
      }, invalidBudget);
      await page.getByRole('button', { name: 'Refresh', exact: true }).click();
      assert.equal(await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).isDisabled(), true);
      const invalidBudgetCalls = await page.evaluate(() => sent.length);
      await awareness.locator('[data-awareness-option=applicationsEnabled]').uncheck();
      assert.equal(await awareness.getByRole('button', { name: 'Save & approve awareness', exact: true }).isDisabled(), true);
      assert.equal(await page.evaluate(() => sent.length), invalidBudgetCalls);
      assert.equal(await page.evaluate(() => fixture.awareness.options.dailyRequestLimit), invalidBudget);
    }
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: path.join(output, 'understanding-fixture.png'), fullPage: true });
    // Exercise layout with the actual shared shell styles and synthetic theme values.
    await setDisclosure(settings, true);
    await setDisclosure(searchTools, true);
    for (const width of [320, 400, 1000]) {
      await page.setViewportSize({ width, height: 900 });
      const overflow = await page.evaluate(() => [...document.querySelectorAll('.understanding-page *')]
        .filter(el => el.checkVisibility() && el.getBoundingClientRect().right > innerWidth)
        .map(el => ({ tag: el.tagName, className: el.getAttribute('class'), right: el.getBoundingClientRect().right })));
      assert.deepEqual(overflow, [], `Overflow at ${width}px: ${JSON.stringify(overflow)}`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    }
    await setDisclosure(settings, false);
    await setDisclosure(searchTools, false);
    await page.screenshot({ path: path.join(output, 'understanding-light.png'), fullPage: true });
    await page.evaluate(() => {
      for (const [name, value] of Object.entries({ page:'#211e1c', paper:'#272320', 'paper-sunk':'#302b27', 'paper-edge':'#39322d',
        ink:'#eee7df', 'ink-2':'#d6cdc4', muted:'#b7aba0', rule:'#494039', 'rule-2':'#60534a', accent:'#ce94ad', 'color-scheme':'dark' }))
        document.documentElement.style.setProperty('--' + name, value);
    });
    await page.screenshot({ path: path.join(output, 'understanding-dark.png'), fullPage: true });
    await setDisclosure(settings, true);
    assert.equal(await page.locator('.understanding-switch').first().evaluate(el => getComputedStyle(el, '::after').transitionDuration), '0s');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.evaluate(() => { document.documentElement.dataset.reduced = 'true'; });
    assert.equal(await page.locator('.understanding-switch').first().evaluate(el => getComputedStyle(el, '::after').transitionDuration), '0s');
    await page.evaluate(() => window.controller.abort());
    assert.equal(await page.locator('button:not(:disabled)').count(), 0);
    assert.equal(await page.locator('input:not(:disabled),textarea:not(:disabled),select:not(:disabled)').count(), 0);
    assert.equal(unexpected, 0);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ passed: true, setting: 'synthetic intercepted host; production assets', realProviderCalls: 0,
      checks: ['automatic setup preserves route, daily limit and independent permissions', 'single page and keyboard disclosures', 'search and topic filtering', 'no-match recovery', 'navigation does not dispatch', 'draft survives view and settings changes', 'remount restores view and unsaved choices', 'separate views', 'point-of-use consent and cancel', 'no implicit reply/external grant', 'correction stays pending', 'draft refresh and stale version', 'exact approve', 'forget cancel', 'inert text',
        'optional awareness host', 'no implicit background approval', 'saved budget preserved when changing sources', 'invalid host budget blocks saving', 'technical metadata excluded from rendering', 'archive accessible through search', 'independent saved-understanding policy', 'explicit window enumeration', 'window choice cancel and revoke', 'inert window titles', 'stale model binding', 'narrow', 'failure', 'dispose'], unexpectedRequests: unexpected }, null, 2));
    process.stdout.write('Understanding browser fixture passed; real provider calls 0.\n');
  } finally { await browser.close(); }
})().catch(error => { process.stderr.write((error.stack || String(error)) + '\n'); process.exitCode = 1; });
