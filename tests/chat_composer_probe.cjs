// Production assets in installed Edge; synthetic host only. No model/network calls.
// node tests/chat_composer_probe.cjs <playwright-module> <ignored-output-dir>
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
const root = path.resolve(__dirname, '../plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat');
const output = path.resolve(process.argv[3]);
fs.mkdirSync(output, { recursive: true });
const record = (stage, result) => {
  const line = JSON.stringify({ time: new Date().toISOString(), request: 'synthetic-composer', stage, result });
  fs.appendFileSync(path.join(output, 'events.jsonl'), line + '\n');
  console.log(line);
};
const server = http.createServer((request, response) => {
  const relative = new URL(request.url, 'http://localhost').pathname.slice(1) || 'index.html';
  const assetRoot = ['margin.css', 'margin-select.js'].includes(relative)
    ? path.resolve(root, '../../../../../src/FgoPet.UiSdk/Resources/Web') : root;
  const file = path.resolve(assetRoot, relative);
  if (!file.startsWith(assetRoot + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end(); return;
  }
  response.writeHead(200, { 'Content-Type': { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' }[path.extname(file)] || 'application/octet-stream' });
  response.end(fs.readFileSync(file));
});
(async () => {
  let browser;
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    record('session.start', 'started');
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 376, height: 500 }, reducedMotion: process.argv[4] || 'no-preference' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => { if (response.status() >= 400) errors.push(`HTTP ${response.status()}: ${new URL(response.url()).pathname}`); });
    await page.addInitScript(() => {
      const state = window.fixture = {
        version: 1, identity: { sessionId: 'fixture-session', servantId: 'fixture-role', conversationId: 'one' },
        draft: { text: '', revision: 0 }, presentation: { roleName: 'Synthetic companion' },
        conversation: { canSend: true, isStreaming: false, canStop: false,
          turns: Array.from({ length: 20 }, (_, i) => ({ id: `turn-${i}`, role: i % 2 ? 'assistant' : 'user', text: `Synthetic ${i}\nA line for reading anchors.\n中文与 English 布局测试。` })) },
        history: { items: [] }
      };
      window.commands = [];
      window.behavior = { sendDelay:0, stopDelay:0, copyDelay:0, copySuccess:true, copies:0 };
      let receive;
      window.pushFixture = () => { state.version++; receive({ data: { type: 'chat.snapshot', payload: structuredClone(state) } }); };
      window.chrome ??= {};
      window.chrome.webview = {
        addEventListener(type, callback) { receive = callback; },
        postMessage(message) {
          if (!message.requestId) return;
          window.commands.push(message.type);
          if (message.type === 'chat.draft') state.draft = { text: message.payload.text, revision: message.payload.revision };
          if (message.type === 'chat.send') {
            state.draft = { text: '', revision: state.draft.revision + 1 };
            Object.assign(state.conversation, { isStreaming: true, canStop: true, canSend: false });
          }
          if (message.type === 'chat.stop') Object.assign(state.conversation, { isStreaming: false, canStop: false, canSend: true });
          state.version++;
          const isCopy = message.type === 'chat.host' && message.payload.action === 'copyTurn';
          if (isCopy) window.behavior.copies++;
          const success = !isCopy || window.behavior.copySuccess;
          const delay = isCopy ? window.behavior.copyDelay : message.type === 'chat.send' ? window.behavior.sendDelay : message.type === 'chat.stop' ? window.behavior.stopDelay : 0;
          setTimeout(() => receive({ data: { type: 'command.result', requestId: message.requestId, success,
            errorCode: success ? undefined : 'CHAT_HOST_ACTION_FAILED', payload: success ? { snapshot: structuredClone(state) } : undefined } }), delay);
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => !document.getElementById('composer').disabled);
    const settle = () => page.evaluate(() => new Promise(resolve => {
      let frames = 8;
      const tick = () => --frames ? requestAnimationFrame(tick) : resolve();
      requestAnimationFrame(tick);
    }));
    const input = async text => { await page.locator('#composer').fill(text); await settle(); };
    const metrics = () => page.evaluate(() => {
      const c = document.getElementById('composer'), s = document.getElementById('message-scroll'), j = document.getElementById('jump-bottom');
      const cr = c.getBoundingClientRect(), sr = s.getBoundingClientRect(), jr = j.getBoundingClientRect();
      const a = Array.from(document.querySelectorAll('#messages article')).find(n => n.getBoundingClientRect().bottom > sr.top + 1);
      return { height: cr.height, bottom: cr.bottom, gap: s.scrollHeight - s.scrollTop - s.clientHeight,
        top: s.scrollTop, anchor: a?.dataset.turnId, offset: a ? a.getBoundingClientRect().top - sr.top : 0,
        jumpHidden: j.hidden, jumpTop: jr.top, jumpBottom: jr.bottom, readingBottom: sr.bottom, composerTop: cr.top,
        overflow: c.scrollHeight > c.clientHeight, horizontal: document.documentElement.scrollWidth > innerWidth };
    });
    const assertAnchor = (before, after) => {
      assert.equal(after.anchor, before.anchor);
      assert.ok(Math.abs(after.offset - before.offset) < 2, JSON.stringify({ before, after }));
    };
    await settle(); assert.equal((await metrics()).height, 34);
    await input('First line\n第二行\nThird line');
    assert.equal((await metrics()).height, 74); assert.ok((await metrics()).gap <= 2);
    await input('Synthetic paste 中文\n'.repeat(40));
    assert.ok((await metrics()).height > 106); assert.equal((await metrics()).overflow, true);
    assert.ok((await metrics()).gap <= 2);
    await page.screenshot({ path: path.join(output, 'bounded-input.png') });
    await input('short'); assert.equal((await metrics()).height, 34);
    record('input.grow-cap-shrink-follow', 'passed');

    await page.evaluate(() => document.getElementById('message-scroll').scrollTo({ top: 450, behavior: 'instant' }));
    await settle(); const reading = await metrics();
    await input('Reading old content\n'.repeat(8));
    assertAnchor(reading, await metrics());
    assert.equal((await metrics()).jumpHidden, false);
    assert.ok((await metrics()).jumpTop >= (await metrics()).readingBottom);
    assert.ok((await metrics()).jumpBottom <= (await metrics()).composerTop);
    await page.evaluate(() => { fixture.conversation.turns.at(-1).text += '\nLong streamed answer.'.repeat(200); pushFixture(); });
    await settle(); assertAnchor(reading, await metrics());
    await page.screenshot({ path: path.join(output, 'reading-during-stream.png') });
    record('reading.input-and-stream-anchor', 'passed');
    // Empty context takes no space; real actions, selections and valid chips stay reachable.
    assert.equal(await page.locator('#context-bar').isVisible(), false);
    for (const context of ['action', 'selection', 'chip', 'empty']) {
      const beforeContext = await metrics();
      await page.evaluate(context => {
        fixture.presentation.actions = { canChooseWorkspace: context === 'action' };
        fixture.presentation.workspaceLabel = context === 'selection' ? 'Synthetic workspace' : '';
        fixture.conversation.contextChips = context === 'chip'
          ? [{ id: 'fixture-context', label: 'Synthetic context' }] : [];
        pushFixture();
      }, context);
      await settle();
      assert.equal(await page.locator('#context-bar').isVisible(), context !== 'empty');
      assertAnchor(beforeContext, await metrics());
    }
    record('context.empty-hidden-available-visible-anchor', 'passed');

    await page.setViewportSize({ width: 328, height: 320 }); await settle();
    assertAnchor(reading, await metrics());
    assert.ok((await metrics()).height <= 320 * .55); assert.ok((await metrics()).bottom <= 320);
    assert.equal((await metrics()).horizontal, false);
    assert.ok((await metrics()).jumpTop >= (await metrics()).readingBottom);
    assert.ok((await metrics()).jumpBottom <= (await metrics()).composerTop);
    await page.setViewportSize({ width: 800, height: 600 }); await settle();
    const beforeSidebar = await metrics();
    await page.locator('#history-toggle').click(); await settle();
    assertAnchor(beforeSidebar, await metrics());
    await page.locator('#history-close').click(); await settle();
    record('resize.and-sidebar', 'passed');

    await page.locator('#jump-bottom').click(); await settle(); assert.ok((await metrics()).gap <= 2);
    await page.evaluate(() => { fixture.conversation.turns.at(-1).text += '\nNew answer line.'.repeat(80); pushFixture(); });
    await settle(); assert.ok((await metrics()).gap <= 2);
    await page.locator('#composer').focus();
    await page.keyboard.press('Shift+Enter'); await settle();
    assert.equal(await page.evaluate(() => commands.filter(x => x === 'chat.send').length), 0);
    await page.evaluate(() => {
      const c = document.getElementById('composer');
      c.dispatchEvent(new CompositionEvent('compositionstart'));
      c.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true }));
      c.dispatchEvent(new CompositionEvent('compositionend'));
    });
    assert.equal(await page.evaluate(() => commands.filter(x => x === 'chat.send').length), 0);
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.getElementById('composer').value === ''); await settle();
    assert.equal((await metrics()).height, 34);
    await page.locator('#stop-generation').click(); await settle();
    await input('Send again\nSecond line'); await page.locator('#send-message').click();
    await page.waitForFunction(() => commands.filter(x => x === 'chat.send').length === 2);
    await page.locator('#stop-generation').click(); await settle();
    record('ime-guard.shift-enter.send-stop-resend', 'passed');

    await page.evaluate(() => { fixture.identity.conversationId = 'two'; fixture.draft = { text: 'Restored draft\n第二行\nThird line', revision: 100 }; pushFixture(); });
    await settle(); assert.equal((await metrics()).height, 74);
    await page.evaluate(() => { fixture.identity.conversationId = 'three'; fixture.draft = { text: '', revision: 101 }; pushFixture(); });
    await settle(); assert.equal((await metrics()).height, 34);
    await page.setViewportSize({ width: 376, height: 500 }); await settle();
    record('conversation.draft-restore-clear', 'passed');
    // User bubbles must fit their text, independently of the action row width.
    await page.evaluate(() => {
      fixture.identity.conversationId = 'bubble-layout';
      fixture.conversation.turns = [
        { id: 'short-one', role: 'user', text: '好', canCopy: true },
        { id: 'short-two', role: 'user', text: '收到，谢谢你。' },
        { id: 'multiline', role: 'user', text: '第一行\n第二行\nThird line' },
        { id: 'url', role: 'user', text: 'https://example.invalid/' + 'long-path-without-spaces/'.repeat(15) },
        { id: 'unbroken', role: 'user', text: 'LongUnbrokenIdentifier'.repeat(25) },
        { id: 'code', role: 'user', text: '```js\n  const value = "' + 'x'.repeat(160) + '";\n  return value;\n```' },
        { id: 'reply', role: 'assistant', text: '角色回复保持原有左侧排版。\nAssistant text stays unchanged.' }
      ];
      pushFixture();
    });
    const bubbleObservations = [];
    for (const width of [376, 328, 800]) {
      await page.setViewportSize({ width, height: width === 328 ? 320 : 600 }); await settle();
      await page.evaluate(() => document.getElementById('message-scroll').scrollTo({ top: 0, behavior: 'instant' }));
      await settle();
      await page.screenshot({ path: path.join(output, `user-bubbles-${width}.png`) });
      const layout = await page.evaluate(() => {
        const thread = document.getElementById('message-scroll'), tr = thread.getBoundingClientRect(), ts = getComputedStyle(thread);
        const left = tr.left + parseFloat(ts.paddingLeft);
        const right = tr.left + thread.clientWidth - parseFloat(ts.paddingRight);
        const users = [...document.querySelectorAll('.turn.user .chat-message-text')].map(p => {
          const r = p.getBoundingClientRect(), style = getComputedStyle(p);
          const expected = fixture.conversation.turns.find(t => t.id === p.parentElement.dataset.turnId).text;
          return { id: p.parentElement.dataset.turnId, width: r.width, left: r.left, right: r.right,
            align: style.textAlign, overflow: p.scrollWidth > p.clientWidth + 1,
            textPreserved: p.textContent === expected, plainText: p.childElementCount === 0 };
        });
        const reply = document.querySelector('.turn.her .chat-message-text').getBoundingClientRect();
        return { left, right, users, reply: { left: reply.left, right: reply.right },
          horizontal: thread.scrollWidth > thread.clientWidth + 1 || document.documentElement.scrollWidth > innerWidth };
      });
      bubbleObservations.push({ width, ...layout });
      fs.writeFileSync(path.join(output, 'bubble-layout.json'), JSON.stringify(bubbleObservations, null, 2));
      assert.equal(layout.users.length, 6);
      assert.equal(layout.horizontal, false);
      for (const user of layout.users) {
        assert.ok(Math.abs(user.right - layout.right) < 2, JSON.stringify(user));
        assert.ok(user.width <= (layout.right - layout.left) * .82 + 2);
        assert.ok(['left', 'start'].includes(user.align));
        assert.equal(user.overflow, false);
        assert.equal(user.textPreserved, true);
        assert.equal(user.plainText, true);
      }
      assert.ok(layout.users[0].width < layout.users[1].width, 'Short bubbles must follow content width');
      assert.ok(layout.users[1].width < layout.users[3].width, 'Long messages must use the bounded available width');
      assert.ok(Math.abs(layout.reply.left - layout.left) < 2);
      assert.ok(Math.abs(layout.reply.right - layout.right) < 2);
    }
    await page.mouse.move(0, 0);
    const copy = page.locator('.turn-copy').first();
    const copyTurn = page.locator('.turn.user').first();
    const copyBounds = await copyTurn.boundingBox();
    assert.equal(await copy.evaluate(n => getComputedStyle(n).opacity), '0');
    await copyTurn.hover();
    assert.equal(await copy.evaluate(n => getComputedStyle(n).opacity), '1');
    assert.deepEqual(await copyTurn.boundingBox(), copyBounds);
    await page.mouse.move(0, 0); await copy.focus();
    assert.equal(await copy.evaluate(n => getComputedStyle(n).opacity), '1');
    assert.deepEqual(await copyTurn.boundingBox(), copyBounds);
    assert.match(await page.locator('#role-name').evaluate(n => getComputedStyle(n).fontFamily), /Segoe UI/);
    record('copy.hover-keyboard-no-layout-shift-ui-title', 'passed');
    // Confirmed copy feedback stays reachable, acknowledges success only, and resets.
    await page.evaluate(() => { behavior.copyDelay=220; behavior.copySuccess=true; });
    const copyCount=await page.evaluate(() => behavior.copies);
    await copy.click();
    await page.waitForFunction(() => document.querySelector('.turn-copy').getAttribute('aria-label') === 'Copying…');
    await copy.click();assert.equal(await page.evaluate(() => behavior.copies),copyCount+1);
    assert.equal(await copy.locator('use').getAttribute('href'), '#icon-copy');
    await page.waitForFunction(() => document.querySelector('.turn-copy').getAttribute('aria-label') === 'Copied');
    assert.equal(await copy.locator('use').getAttribute('href'), '#icon-check');
    await page.screenshot({path:path.join(output,'copy-confirmed.png')});
    assert.equal(await page.locator('#composer-feedback').textContent(), 'Copied');
    assert.equal(await copy.evaluate(n => document.activeElement === n), true);
    await page.waitForFunction(() => document.querySelector('.turn-copy').getAttribute('aria-label') === 'Copy message', { timeout:4000 });
    await page.evaluate(() => { behavior.copySuccess=false; });
    await copy.click();await page.waitForFunction(() => !document.getElementById('page-status').hidden);
    assert.equal(await copy.locator('use').getAttribute('href'), '#icon-copy');
    assert.notEqual(await page.locator('#composer-feedback').textContent(), 'Copied');
    // Pending send and stop use the same fixed control position and prevent repeats.
    await page.evaluate(() => { behavior.sendDelay=300; behavior.stopDelay=300; behavior.copyDelay=0; });
    await input('One pending request');const sendPosition=await page.locator('#send-message').boundingBox();
    const sendCount=await page.evaluate(() => commands.filter(t=>t==='chat.send').length);
    await page.locator('#composer').press('Enter');await page.locator('#composer').press('Enter');
    await page.waitForFunction(() => document.getElementById('send-message').getAttribute('aria-busy') === 'true');
    assert.equal(await page.locator('#composer').isEnabled(),true);
    assert.equal(await page.locator('#send-message').isDisabled(),true);
    await page.waitForFunction(() => !document.getElementById('stop-generation').hidden);
    assert.equal(await page.evaluate(() => commands.filter(t=>t==='chat.send').length),sendCount+1);
    assert.deepEqual(await page.locator('#stop-generation').boundingBox(),sendPosition);
    await page.evaluate(() => { fixture.conversation.canStop=false;pushFixture(); });await settle();
    assert.equal(await page.locator('#stop-generation').isVisible(),true);assert.equal(await page.locator('#stop-generation').isDisabled(),true);
    const stopCount=await page.evaluate(() => commands.filter(t=>t==='chat.stop').length);
    await page.evaluate(() => document.getElementById('stop-generation').click());assert.equal(await page.evaluate(() => commands.filter(t=>t==='chat.stop').length),stopCount);
    await page.evaluate(() => { fixture.conversation.canStop=true;pushFixture(); });await settle();
    await page.locator('#stop-generation').click();await page.evaluate(() => document.getElementById('stop-generation').click());
    assert.equal(await page.locator('#stop-generation').getAttribute('aria-label'),'Stopping…');
    await page.screenshot({path:path.join(output,'stop-pending.png')});
    await page.waitForFunction(() => document.getElementById('stop-generation').hidden);
    assert.equal(await page.evaluate(() => commands.filter(t=>t==='chat.stop').length),stopCount+1);
    await input(' ');assert.equal(await page.locator('#send-message').isDisabled(),true);
    await input('Ready again');assert.equal(await page.locator('#send-message').isEnabled(),true);
    await page.locator('#send-message').focus();assert.equal(await page.locator('#chat-tooltip').isVisible(),true);
    await page.keyboard.press('Escape');assert.equal(await page.locator('#chat-tooltip').isVisible(),false);
    assert.equal(await page.locator('#send-message').evaluate(n=>document.activeElement===n),true);
    const buttonBottom=(await page.locator('#send-message').boundingBox()).y+34;
    await input('A longer draft\n'.repeat(60));const grown=await metrics();
    assert.ok(grown.height>106);assert.equal((await page.locator('#send-message').boundingBox()).y+34,buttonBottom);
    assert.ok((await page.locator('.thread-viewport').boundingBox()).height>=72);
    assert.ok((await page.locator('.composer-toolbar').boundingBox()).y>=grown.bottom);
    await input('x'.repeat(12001));
    assert.equal(await page.locator('#send-message').isDisabled(),true);
    assert.equal(await page.locator('#composer').getAttribute('aria-invalid'),'true');
    assert.equal((await page.locator('#send-message').boundingBox()).y+34,buttonBottom);
    const invalidSendCount=await page.evaluate(()=>commands.filter(t=>t==='chat.send').length);
    await page.locator('#composer').press('Enter');await page.evaluate(()=>document.getElementById('send-message').click());
    assert.equal(await page.evaluate(()=>commands.filter(t=>t==='chat.send').length),invalidSendCount);
    await input('Ready after correction');assert.equal(await page.locator('#composer-error').isVisible(),false);
    // A late copy acknowledgement belongs to the old conversation only.
    await page.evaluate(()=>{behavior.copySuccess=true;behavior.copyDelay=220;});
    await copy.click();await page.evaluate(()=>{fixture.identity.conversationId='copy-next-conversation';pushFixture();});
    await page.waitForTimeout(250);assert.notEqual(await page.locator('#composer-feedback').textContent(),'Copied');
    record('actions.copy-success-failure-submit-stop-tooltip', 'passed');

    record('messages.user-bubbles-fit-wrap-right', 'passed');
    assert.deepEqual(errors, []);
    record('session.complete', 'passed');
  } catch (error) {
    record('session.complete', 'failed');
    throw error;
  } finally { await browser?.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
