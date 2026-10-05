// Real SDK/model smoke test. Probe hooks are injected into the served test copy only.
// Usage: node tests/live2d_runtime_probe.cjs <runtime> <model-dir> <playwright-module> <output> [playback-frames.json]
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[4]);
const runtime = path.resolve(process.argv[2]);
const modelRoot = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[5]);
fs.mkdirSync(output, { recursive: true });
const record = (stage, result) => {
  const event = { time: new Date().toISOString(), request: 'live2d-local-probe', stage, result };
  fs.appendFileSync(path.join(output, 'events.jsonl'), JSON.stringify(event) + '\n');
  console.log(JSON.stringify(event));
};
const server = http.createServer((request, response) => {
  const route = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
  const modelRoute = route.startsWith('/Resources/mash_casual/');
  const root = modelRoute ? modelRoot : runtime;
  const relative = modelRoute ? route.slice('/Resources/mash_casual/'.length) : route.slice(1) || 'index.html';
  const file = path.resolve(root, relative);
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end(); return;
  }
  let data = fs.readFileSync(file);
  const type = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.png': 'image/png' }[path.extname(file)] || 'application/octet-stream';
  if (!modelRoute && relative === 'app.js') {
    const text = data.toString();
    assert.match(text, /\}\)\(\);\s*$/);
    data = text.replace(/\}\)\(\);\s*$/, 'window.__probe = { delegate: LAppDelegate, behavior: desktopBehavior, state: typeof desktopState === "undefined" ? null : desktopState };\n})();');
  } else if (!modelRoute && relative === 'index.html') {
    data = data.toString().replace('<head>', `<head><script>
      window.__messages = []; window.chrome ??= {};
      window.chrome.webview = { postMessage(message) { window.__messages.push(message); },
        addEventListener(type, callback) { window.__receive = callback; } };
    </script>`);
  }
  response.writeHead(200, { 'Content-Type': type }).end(data);
});

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser, page;
  try {
    record('session.start', 'started');
    browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--enable-unsafe-swiftshader'] });
    page = await browser.newPage({ viewport: { width: 531, height: 675 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => window.__messages.some(m => m.type === 'live2d.ready'), { timeout: 20000 });
    await page.waitForFunction(() => window.__messages.some(m => m.type === 'live2d.hitmask' && /[^A]/.test(m.data)), { timeout: 20000 });
    record('model.render', 'ready-with-hitmask');
    const drive = async (x, y) => {
      await page.evaluate(({ x, y }) => {
        clearInterval(window.__pointerTimer);
        window.__pointerTimer = setInterval(() => window.__receive({ data: { type: 'live2d.pointer', x, y, active: true } }), 33);
      }, { x, y });
      await page.waitForTimeout(1000);
      return page.evaluate(() => {
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        const core = owner.getModel().getModel();
        return { eye: core.parameters.values[core.parameters.ids.indexOf('ParamEyeBallX')],
          head: core.parameters.values[core.parameters.ids.indexOf('ParamAngleX')], origin: owner._desktopEyeViewport };
      });
    };
    const left = await drive(-2, 0.25);
    await page.screenshot({ path: path.join(output, 'left.png'), omitBackground: true });
    const right = await drive(3, 0.25);
    await page.screenshot({ path: path.join(output, 'right.png'), omitBackground: true });
    fs.writeFileSync(path.join(output, 'gaze.json'), JSON.stringify({ left, right, errors }, null, 2));
    assert.deepEqual(errors, []);
    assert.ok(left.eye < -0.8 && right.eye > 0.8);
    assert.ok(left.head < -4 && right.head > 4);
    assert.ok(right.origin.y > 0 && right.origin.y < 0.5);
    record('pointer.dispatch-and-gaze', 'left-right-passed');
    await drive(right.origin.x, -3);
    await page.screenshot({ path: path.join(output, 'look-up.png'), omitBackground: true });
    const clearTop = await page.evaluate(() => {
      const mask = window.__messages.filter(m => m.type === 'live2d.hitmask').at(-1);
      const bytes = atob(mask.data);
      for (let i = 0; i < mask.width * 2 / 8; i++) if (bytes.charCodeAt(i) !== 0) return false;
      return true;
    });
    assert.equal(clearTop, true);
    record('viewport.look-up', 'top-margin-passed');
    await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      window.__originalFinished = owner._motionManager.isFinished;
      owner._motionManager.isFinished = () => false;
      const model = owner.getModel(), core = model.getModel();
      model.setParameterValueByIndex(core.parameters.ids.indexOf('ParamAngleY'), 30);
      model.saveParameters();
    });
    await page.waitForTimeout(500);
    await page.screenshot({ path: path.join(output, 'max-look-up.png'), omitBackground: true });
    assert.equal(await page.evaluate(() => {
      const mask = window.__messages.filter(m => m.type === 'live2d.hitmask').at(-1), bytes = atob(mask.data);
      for (let i = 0; i < mask.width * 2 / 8; i++) if (bytes.charCodeAt(i) !== 0) return false;
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      owner._motionManager.isFinished = window.__originalFinished;
      const model = owner.getModel();
      for (let i = 0; i < model.getParameterCount(); i++) model.setParameterValueByIndex(i, model.getParameterDefaultValue(i));
      model.saveParameters();
      return true;
    }), true);
    record('viewport.max-head-y', 'top-margin-passed');
    await page.evaluate(() => {
      window.__probe.behavior.attendedAt = performance.now() / 1000 - 10;
      clearInterval(window.__pointerTimer);
      window.__receive({ data: { type: 'live2d.pointer', x: 3, y: 0.25, active: false } });
      window.__pointerTimer = setInterval(() => window.__receive({ data: { type: 'live2d.pointer', x: 3, y: 0.25, active: false } }), 33);
    });
    await page.waitForTimeout(1500);
    const idle = await page.evaluate(() => window.__probe.behavior.eyeX);
    assert.ok(Math.abs(idle) < 0.4);
    record('attention.transition', 'idle-passed');
    const regionAudit = await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      const core = owner.getModel().getModel(), region = owner._desktopHeadRegion;
      const original = region.indices;
      const hits = original.filter(i => { region.indices = [i]; return region.isHit({ x: owner._desktopEyeViewport.x + 0.04, y: 0.85 }, owner._desktopViewMatrix); });
      region.indices = original;
      return hits.map(i => ({ id: core.drawables.ids[i], part: core.parts.ids[core.drawables.parentPartIndices[i]],
        vertices: Array.from(core.drawables.vertexPositions[i]) }));
    });
    fs.writeFileSync(path.join(output, 'head-region.json'), JSON.stringify(regionAudit));
    await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      const point = { x: owner._desktopEyeViewport.x + 0.04, y: owner._desktopEyeViewport.y };
      if (!owner._desktopHeadRegion.isHit(point, owner._desktopViewMatrix)) throw new Error('Head mesh did not contain face point');
      if (owner._desktopHeadRegion.isHit({ x: point.x, y: 0.85 }, owner._desktopViewMatrix)) throw new Error('Body counted as head');
      clearInterval(window.__pointerTimer);
      window.__pointerTimer = setInterval(() => window.__receive({ data: { type: 'live2d.pointer', ...point, active: false } }), 33);
    });
    await page.waitForFunction(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      return owner._desktopHoverFrame.state === 'reaction' && owner._desktopHoverFrame.blink < 0.2;
    }, null, { timeout: 5000 });
    const hover = await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      const p = owner.getModel().getModel().parameters;
      return { tilt: owner._desktopHoverFrame.tilt, leftEye: p.values[p.ids.indexOf('ParamEyeLOpen')],
        rightEye: p.values[p.ids.indexOf('ParamEyeROpen')], reactedAt: owner._desktopHover.reactedAt };
    });
    assert.ok(hover.tilt > 0.5 && hover.tilt < 3);
    assert.ok(hover.leftEye < 0.25 && hover.rightEye < 0.25);
    await page.waitForTimeout(230);
    await page.screenshot({ path: path.join(output, 'hover.png'), omitBackground: true });
    record('head.hover-reaction', 'tilt-and-blink-passed');
    await page.waitForTimeout(800);
    const held = await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      owner._desktopHover.cooldownUntil = 0; // Ensure one-shot behavior even after cooldown expires.
      return owner._desktopHover.reactedAt;
    });
    await page.waitForTimeout(800);
    assert.equal(await page.evaluate(() => window.__probe.delegate.getInstance()._subdelegates[0]
      .getLive2DManager()._models[0]._desktopHover.reactedAt), held);
    // Leave and re-enter so Tap interrupts a real second hover reaction.
    await page.evaluate(() => {
      clearInterval(window.__pointerTimer);
      window.__receive({ data: { type: 'live2d.pointer', x: 3, y: 0.8, active: false } });
    });
    await page.waitForTimeout(450);
    assert.ok(await page.evaluate(() => Math.abs(window.__probe.delegate.getInstance()._subdelegates[0]
      .getLive2DManager()._models[0]._desktopHoverFrame.tilt) < 0.05));
    await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      const point = { x: owner._desktopEyeViewport.x + 0.04, y: owner._desktopEyeViewport.y };
      window.__pointerTimer = setInterval(() => window.__receive({ data: { type: 'live2d.pointer', ...point, active: false } }), 33);
    });
    await page.waitForFunction(() => window.__probe.delegate.getInstance()._subdelegates[0]
      .getLive2DManager()._models[0]._desktopHoverFrame.state === 'reaction', null, { timeout: 5000 });
    record('head.hover-lifecycle', 'one-shot-and-reentry-passed');
    // Tap uses actual SDK motion and suppresses look offsets until completion.
    await page.evaluate(() => window.__receive({ data: { type: 'live2d.tap' } }));
    await page.waitForTimeout(150);
    const tap = await page.evaluate(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      return { playing: !owner._motionManager.isFinished(), weight: owner._desktopGazeWeight,
        hoverState: owner._desktopHoverFrame.state, hoverTilt: owner._desktopHoverFrame.tilt, hoverBlink: owner._desktopHoverFrame.blink };
    });
    assert.equal(tap.playing, true);
    assert.equal(tap.weight, 0);
    assert.equal(tap.hoverState, 'suppressed');
    assert.equal(tap.hoverTilt, 0);
    assert.equal(tap.hoverBlink, 1);
    record('tap.motion-ownership', 'passed');
    await page.waitForFunction(() => {
      const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
      return owner._motionManager.isFinished() && owner._desktopGazeWeight === 1;
    }, null, { timeout: 20000 });
    record('tap.motion-complete', 'gaze-restored');
    if (await page.evaluate(() => window.__probe.state && window.__probe.delegate.getInstance()
      ._subdelegates[0].getLive2DManager()._models[0]._modelSetting.getMotionCount('Thinking') > 0)) {
      await page.evaluate(() => {
        clearInterval(window.__pointerTimer);
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        window.__starts = [];
        const start = owner.startMotion.bind(owner);
        owner.startMotion = (group, ...args) => { window.__starts.push(group); return start(group, ...args); };
        window.__receive({ data: { type: 'live2d.thinking', active: true } });
      });
      await page.waitForFunction(() => {
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        return window.__probe.state.state === 'thinking' && owner._motionManager.isFinished() && window.__probe.state.pose;
      });
      await page.evaluate(() => window.__receive({ data: { type: 'live2d.thinking', active: true } }));
      await page.waitForTimeout(700);
      assert.deepEqual(await page.evaluate(() => window.__starts), ['Thinking']);
      assert.equal(await page.evaluate(() => window.__probe.delegate.getInstance()._subdelegates[0]
        .getLive2DManager()._models[0]._desktopGazeWeight), 0);
      await page.screenshot({ path: path.join(output, 'thinking-held.png'), omitBackground: true });
      record('state.thinking', 'one-shot-held');
      await page.evaluate(() => {
        window.__receive({ data: { type: 'live2d.thinking', active: false } });
        window.__receive({ data: { type: 'live2d.expression', key: 'shy' } });
      });
      await page.waitForFunction(() => {
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        const core = owner.getModel().getModel();
        return window.__probe.state.state === 'shy' && core.parameters.values[core.parameters.ids.indexOf('ParamCheek')] > .9;
      });
      await page.screenshot({ path: path.join(output, 'shy-blush.png'), omitBackground: true });
      await page.waitForFunction(() => window.__probe.state.state === 'neutral');
      await page.waitForTimeout(2000);
      const cheek = await page.evaluate(() => {
        const core = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0].getModel().getModel();
        return core.parameters.values[core.parameters.ids.indexOf('ParamCheek')];
      });
      assert.ok(cheek < 0.05);
      await page.screenshot({ path: path.join(output, 'shy-released.png'), omitBackground: true });
      record('state.shy', 'blush-then-gradual-default-recovery');
      await page.evaluate(() => {
        window.__receive({ data: { type: 'live2d.expression', key: 'neutral' } });
        window.__probe.state.lastActiveAt = performance.now() / 1000 - 601;
      });
      await page.waitForFunction(() => {
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        return window.__probe.state.state === 'sleep' && owner._motionManager.isFinished();
      });
      await page.evaluate(() => window.__receive({ data: { type: 'live2d.pointer', x: 3, y: .2, active: true } }));
      await page.waitForTimeout(1200);
      const sleep = await page.evaluate(() => {
        const owner = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0];
        const core = owner.getModel().getModel();
        return { state: window.__probe.state.state, gaze: owner._desktopGazeWeight,
          eyes: ['ParamEyeLOpen', 'ParamEyeROpen'].map(id => core.parameters.values[core.parameters.ids.indexOf(id)]),
          hover: owner._desktopHoverFrame.state };
      });
      assert.equal(sleep.state, 'sleep'); assert.equal(sleep.gaze, 0);
      assert.ok(sleep.eyes.every(v => v < .01)); assert.equal(sleep.hover, 'suppressed');
      await page.screenshot({ path: path.join(output, 'sleep-held.png'), omitBackground: true });
      record('state.sleep', 'idle-entered-eyes-held-no-pointer-wake');
      const beforeWake = await page.evaluate(() => window.__starts.length);
      await page.evaluate(() => window.__receive({ data: { type: 'live2d.tap' } }));
      await page.waitForFunction(() => window.__probe.state.state === 'neutral'
        && window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0]._desktopGazeWeight === 1);
      await page.waitForTimeout(1200);
      assert.equal(await page.evaluate(() => window.__starts.length), beforeWake);
      const awake = await page.evaluate(() => {
        const core = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0].getModel().getModel();
        return core.parameters.values[core.parameters.ids.indexOf('ParamCheek')];
      });
      assert.ok(awake < .05);
      record('state.wake', 'tap-releases-pose-without-random-motion');
      await page.evaluate(() => window.__receive({ data: { type: 'live2d.expression', key: 'happy' } }));
      await page.waitForTimeout(300);
      assert.ok(await page.evaluate(() => {
        const core = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0].getModel().getModel();
        return core.parameters.values[core.parameters.ids.indexOf('ParamMouthForm')] > .4;
      }));
      record('state.happy', 'procedural-smile');
    }
    if (fs.readFileSync(path.join(runtime, 'runtime-info.json'), 'utf8').includes('"speech_lip_sync"')) {
      await page.evaluate(() => {
        window.__speechPacket = { type: 'live2d.speech', active: true, level: 1 };
        window.__speechTimer = setInterval(() => window.__receive({ data: window.__speechPacket }), 33);
      });
      await page.waitForTimeout(500);
      const mouth = () => page.evaluate(() => {
        const core = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0].getModel().getModel();
        return { open: core.parameters.values[core.parameters.ids.indexOf('ParamMouthOpenY')],
          form: core.parameters.values[core.parameters.ids.indexOf('ParamMouthForm')] };
      });
      const loud = await mouth();
      assert.ok(loud.open > .45 && loud.open <= .56); assert.ok(loud.form > .4);
      await page.screenshot({ path: path.join(output, 'speech-open.png'), omitBackground: true });
      await page.evaluate(() => window.__speechPacket.level = 0);
      await page.waitForTimeout(400);
      assert.ok((await mouth()).open < .03);
      await page.evaluate(() => {
        clearInterval(window.__speechTimer);
        window.__receive({ data: { type: 'live2d.speech', active: false, level: 0 } });
      });
      await page.waitForTimeout(900);
      assert.ok((await mouth()).form > .4);
      record('speech.sound-silence-stop', 'mouth-bounded-expression-retained');
      await page.evaluate(() => {
        window.__speechPacket.level = 1;
        window.__receive({ data: window.__speechPacket });
      });
      await page.waitForTimeout(1400);
      assert.ok((await mouth()).open < .03);
      record('speech.stale-packets', 'mouth-released');
      if (process.argv[6]) {
        const frames = JSON.parse(fs.readFileSync(path.resolve(process.argv[6]), 'utf8'));
        assert.ok(frames.length > 3 && frames.some(frame => frame.active && frame.level > .1));
        assert.ok(frames.every(frame => Number.isFinite(frame.seconds) && frame.seconds >= 0
          && typeof frame.active === 'boolean' && Number.isFinite(frame.level) && frame.level >= 0 && frame.level <= 1));
        const response = await page.evaluate(frames => new Promise(resolve => {
          const started = performance.now() / 1000;
          const first = frames[0].seconds;
          let index = 0, peak = 0;
          const timer = setInterval(() => {
            const elapsed = performance.now() / 1000 - started + first;
            while (index + 1 < frames.length && frames[index + 1].seconds <= elapsed) index++;
            const frame = frames[index];
            window.__receive({ data: { type: 'live2d.speech', active: frame.active, level: frame.level } });
            const core = window.__probe.delegate.getInstance()._subdelegates[0].getLive2DManager()._models[0].getModel().getModel();
            peak = Math.max(peak, core.parameters.values[core.parameters.ids.indexOf('ParamMouthOpenY')]);
            if (index === frames.length - 1) { clearInterval(timer); resolve({ peak, released: !frame.active }); }
          }, 33);
        }), frames);
        assert.ok(response.peak > .04 && response.peak <= .56); assert.equal(response.released, true);
        await page.waitForTimeout(900);
        assert.ok((await mouth()).open < .03);
        record('speech.real-playback-frames', 'tts-energy-rendered-and-released');
      }
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ left, right, idle, hover, tap, errors }, null, 2));
    record('result', 'passed');
  } catch (error) {
    if (page) console.error(await page.evaluate(() => {
      const owner = window.__probe?.delegate.getInstance()._subdelegates[0]?.getLive2DManager()._models[0];
      const state = window.__probe?.state;
      return JSON.stringify({ state: state?.state, hasMotion: state?.hasMotion, pose: !!state?.pose,
        finished: owner?._motionManager.isFinished(), starts: window.__starts,
        errors: window.__errors });
    }).catch(() => 'Probe page unavailable'));
    record('result', 'failed'); throw error;
  }
  finally { if (browser) await browser.close(); server.close(); record('session.exit', 'closed'); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
