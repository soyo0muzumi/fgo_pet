import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopState } from '../plugins/providers/FgoPet.Portrait.Live2D/Runtime/desktop-state.mjs';

const owner = () => ({
  started: [], stopped: 0,
  _modelSetting: { getMotionCount: () => 1 },
  _motions: new Map(['Thinking', 'Shy', 'Concerned', 'Surprised', 'Sleep'].map(g => [g + '_0', { setLoop() {} }])),
  _motionManager: { finished: false, stopAllMotions() {}, isFinished() { return this.finished; } },
  startMotion(group) { this.started.push(group); },
});
test('thinking starts once, yields to a later expression and never replays while held', () => {
  const state = new DesktopState(), model = owner();
  state.receive({ type: 'live2d.thinking', active: true }, 1);
  state.sync(model, 1); state.sync(model, 1000);
  assert.deepEqual(model.started, ['Thinking']);
  assert.equal(state.blocksGaze, true);
  state.receive({ type: 'live2d.expression', key: 'shy' }, 1001);
  state.sync(model, 1001);
  assert.equal(state.state, 'thinking');
  state.receive({ type: 'live2d.thinking', active: false }, 1002);
  state.sync(model, 1002);
  assert.deepEqual(model.started, ['Thinking', 'Shy']);
});
test('idle sleeps after ten minutes; mouse movement does not wake; tap wakes without a random motion', () => {
  const state = new DesktopState(), model = owner();
  state.sync(model, 599); assert.equal(state.state, 'neutral');
  state.sync(model, 600); assert.equal(state.state, 'sleep');
  state.activity(601); state.sync(model, 601); assert.equal(state.state, 'sleep');
  assert.equal(state.tap(602), false);
  state.sync(model, 602); assert.equal(state.state, 'neutral');
  assert.equal(state.tap(603), true);
});
test('active mouse activity postpones sleep and a chat request wakes the sleeper', () => {
  const state = new DesktopState(), model = owner();
  state.activity(590); state.sync(model, 600); assert.equal(state.state, 'neutral');
  state.sync(model, 1190); assert.equal(state.state, 'sleep');
  state.receive({ type: 'live2d.thinking', active: true }, 1191);
  state.sync(model, 1191); assert.equal(state.state, 'thinking');
  assert.equal(state.tap(1192), false);
});
test('invalid messages cannot select states or reset the inactivity timer', () => {
  const state = new DesktopState();
  assert.equal(state.receive({ type: 'live2d.thinking', active: 'yes' }, 20), false);
  assert.equal(state.receive({ type: 'live2d.expression', key: '../Sleep' }, 20), false);
  assert.equal(state.receive({ type: 'live2d.expression', key: 'shy' }, NaN), false);
  assert.equal(state.lastActiveAt, 0);
});

test('actual playback wakes sleep and holds activity until the last audio frame', () => {
  const state = new DesktopState(), model = owner();
  state.sync(model, 600); assert.equal(state.state, 'sleep');
  state.speech(true, 601); state.sync(model, 601); assert.equal(state.state, 'neutral');
  state.speech(true, 1300); state.sync(model, 1300); assert.equal(state.state, 'neutral');
  state.speech(false, 1301); state.sync(model, 1301); assert.equal(state.state, 'neutral');
});
test('a held pose survives finished motion frames; clearing it restores the release path', () => {
  const state = new DesktopState(), actor = owner();
  const values = [10, .7];
  const model = { getParameterCount: () => 2, getParameterValueByIndex: i => values[i],
    setParameterValueByIndex: (i, value) => { values[i] = value; } };
  state.receive({ type: 'live2d.thinking', active: true }, 1); state.sync(actor, 1);
  state.capture(model); values.fill(0); assert.equal(state.applyBase(model), true);
  assert.deepEqual(values, [10, .7]);
  state.receive({ type: 'live2d.thinking', active: false }, 2); state.sync(actor, 2);
  assert.equal(state.applyBase(model), false);
});

test('short named expressions release their pose when finished without replaying', () => {
  for (const key of ['shy', 'concerned', 'sad', 'surprised']) {
    const state = new DesktopState(), actor = owner();
    state.receive({ type: 'live2d.expression', key }, 1); state.sync(actor, 1);
    state.pose = [1]; actor._motionManager.finished = true;
    state.sync(actor, 2); state.sync(actor, 3);
    assert.equal(state.state, 'neutral'); assert.equal(state.expression, 'neutral');
    assert.equal(state.hasMotion, false); assert.equal(state.pose, null);
    assert.equal(state.blocksGaze, false); assert.equal(actor.started.length, 1);
  }
});

test('Thinking and Sleep retain their pose after motion completion', () => {
  for (const thinking of [true, false]) {
    const state = new DesktopState(), actor = owner();
    if (thinking) state.receive({ type: 'live2d.thinking', active: true }, 1);
    state.sync(actor, thinking ? 1 : 600); actor._motionManager.finished = true;
    state.sync(actor, 601);
    assert.equal(state.state, thinking ? 'thinking' : 'sleep');
    assert.equal(state.hasMotion, true); assert.equal(actor.started.length, 1);
  }
});
