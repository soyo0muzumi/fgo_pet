import test from 'node:test';
import assert from 'node:assert/strict';
import { SpeechLipSync } from '../plugins/providers/FgoPet.Portrait.Live2D/Runtime/desktop-speech.mjs';

test('speech is bounded, eases toward sound, closes in silence and releases after stop', () => {
  const lip = new SpeechLipSync();
  lip.receive({ type: 'live2d.speech', active: true, level: 1 }, 1);
  let frame = lip.update(.02, 1);
  assert.ok(frame.open > 0 && frame.open < .55); assert.ok(frame.weight > 0);
  for (let i = 0; i < 10; i++) frame = lip.update(.02, 1 + i * .02);
  assert.ok(frame.open > .45 && frame.open <= .55);
  lip.receive({ type: 'live2d.speech', active: true, level: 0 }, 1.2);
  for (let i = 0; i < 10; i++) frame = lip.update(.02, 1.2 + i * .02);
  assert.ok(frame.open < .04); assert.ok(frame.weight > .95);
  lip.receive({ type: 'live2d.speech', active: false, level: 0 }, 1.4);
  for (let i = 0; i < 40; i++) frame = lip.update(.02, 1.4 + i * .02);
  assert.ok(frame.weight < .005);
});

test('missing packets release speech and invalid messages cannot retain ownership', () => {
  const lip = new SpeechLipSync();
  assert.equal(lip.receive({ type: 'live2d.speech', active: 'yes', level: 1 }, 1), false);
  assert.equal(lip.receive({ type: 'live2d.speech', active: true, level: NaN }, 1), false);
  assert.equal(lip.receive({ type: 'live2d.speech', active: true, level: 2 }, 1), false);
  lip.receive({ type: 'live2d.speech', active: true, level: .8 }, 1);
  assert.equal(lip.active(1.2), true); assert.equal(lip.active(1.6), false);
  const frame = lip.update(1, 2);
  assert.ok(Number.isFinite(frame.open)); assert.ok(frame.weight < .005);
});

test('lip sync only owns mouth opening and restores the expression mouth baseline', () => {
  const lip = new SpeechLipSync();
  const values = { ParamMouthOpenY: .2, ParamMouthForm: .45, ParamCheek: 1 };
  const model = { getModel: () => ({ parameters: { ids: Object.keys(values) } }),
    setParameterValueById: (id, value, weight) => { values[id] += (value - values[id]) * weight; } };
  lip.apply(model, id => id, { open: .55, weight: 1 });
  assert.deepEqual(values, { ParamMouthOpenY: .55, ParamMouthForm: .45, ParamCheek: 1 });
  values.ParamMouthOpenY = .2;
  lip.apply(model, id => id, { open: 0, weight: 0 });
  assert.equal(values.ParamMouthOpenY, .2);
});
