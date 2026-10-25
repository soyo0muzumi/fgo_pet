import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopBehavior, eyeOriginFromModel, projectEyeOrigin, applyGaze, restoreMotionBase, HeadHover, HeadHitRegion, applyHoverBlink } from '../plugins/providers/FgoPet.Portrait.Live2D/Runtime/desktop-behavior.mjs';

const origin = { x: 0.5, y: 0.25 };
const sample = (behavior, x, y, at, active = true) => behavior.receive({ type: 'live2d.pointer', x, y, active }, at);

test('global cursor outside viewport follows relative to eyes, with bounded gradual response', () => {
  const b = new DesktopBehavior(() => 0.5);
  sample(b, 4, -2, 0);
  const first = b.update(1 / 60, 0, origin);
  assert.equal(first.mode, 'follow');
  assert.ok(first.eyeX > 0 && first.eyeX < 1);
  assert.ok(first.eyeY > 0 && first.eyeY < 1);
  assert.ok(first.headX < first.eyeX);
  assert.ok(first.headY < first.eyeY);
  assert.equal(b.update(1 / 60, 0.1, origin).eyeX > first.eyeX, true);
});

test('dead zone is centered on eyes rather than window center', () => {
  const b = new DesktopBehavior(() => 0.5);
  sample(b, 0.51, 0.26, 0);
  const gaze = b.update(0.1, 0, origin);
  assert.equal(gaze.eyeX, 0);
  assert.equal(gaze.eyeY, 0);
});

test('stationary samples release attention through soft follow into random idle', () => {
  const b = new DesktopBehavior(() => 0.9);
  sample(b, 2, 0.5, 0);
  sample(b, 2, 0.5, 5, false);
  assert.equal(b.update(0.1, 5, origin).mode, 'soft');
  sample(b, 2, 0.5, 10, false);
  const gaze = b.update(0.1, 10, origin);
  assert.equal(gaze.mode, 'idle');
  assert.ok(Math.abs(gaze.eyeX) < 0.4);
});

test('entering face proximity restores attention without locking stationary hover forever', () => {
  const b = new DesktopBehavior(() => 0.5);
  sample(b, 3, 3, 10, false);
  assert.equal(b.update(0.1, 10, origin).mode, 'idle');
  sample(b, 0.6, 0.3, 11, false);
  assert.equal(b.update(0.1, 11, origin).mode, 'follow');
  sample(b, 0.6, 0.3, 21, false);
  assert.equal(b.update(0.1, 21, origin).mode, 'idle');
  b.interact(21);
  assert.equal(b.update(0.1, 21, origin).mode, 'follow');
});

test('invalid packets and stale input cannot retain or poison gaze', () => {
  const b = new DesktopBehavior(() => 0.5);
  assert.equal(sample(b, NaN, 1, 0), false);
  assert.equal(b.receive({ type: 'other', x: 1, y: 1, active: true }, 0), false);
  sample(b, 1, 1, 0);
  assert.equal(b.update(0.1, 2, origin).mode, 'idle');
  assert.ok(Number.isFinite(b.update(60, 3, origin).eyeX));
});

test('frame-rate independent smoothing converges without overshoot', () => {
  function run(hz) {
    const b = new DesktopBehavior(() => 0.5);
    for (let i = 0; i < hz; i++) {
      sample(b, 2, 0.25, i / hz);
      b.update(1 / hz, i / hz, origin);
    }
    return b.update(0, 1, origin);
  }
  assert.ok(Math.abs(run(30).headX - run(60).headX) < 0.001);
  assert.ok(run(60).eyeX <= 1);
});

test('eye origin uses both eye parts including descendant meshes and projection', () => {
  const core = {
    parts: { ids: ['Part5', 'Part6', 'child', 'body'], parentIndices: [-1, -1, 0, -1] },
    drawables: { parentPartIndices: [2, 1, 3], vertexPositions: [
      new Float32Array([-0.6, 0.2, -0.2, 0.4]), new Float32Array([0.2, 0.2, 0.6, 0.4]),
      new Float32Array([-2, -2, 2, 2])
    ] }
  };
  const eye = eyeOriginFromModel(core);
  assert.ok(Math.abs(eye.x) < 0.001);
  assert.ok(Math.abs(eye.y - 0.3) < 0.001);
  const projected = projectEyeOrigin(eye, { transformX: x => x + 0.2, transformY: y => y * 2 });
  assert.ok(Math.abs(projected.x - 0.6) < 0.001);
  assert.ok(Math.abs(projected.y - 0.2) < 0.001);
  assert.equal(eyeOriginFromModel({ parts: { ids: [] }, drawables: { parentPartIndices: [] } }), null);
});

test('gaze overrides eyes, adds small head offset, yields during tap, and does not own body/mouth/blink', () => {
  const values = new Map([['ParamEyeBallX', 0.2], ['ParamEyeBallY', 0], ['ParamAngleX', -5], ['ParamAngleY', 0]]);
  const model = {
    getParameterCount: () => values.size,
    getParameterId: i => [...values.keys()][i],
    setParameterValueById: (id, v) => values.set(id, v),
    addParameterValueById: (id, v) => values.set(id, values.get(id) + v)
  };
  const gaze = { eyeX: 1, eyeY: 0.5, headX: 0.5, headY: 0.25 };
  applyGaze(model, id => id, gaze, 1);
  assert.equal(values.get('ParamEyeBallX'), 1);
  assert.equal(values.get('ParamAngleX'), 1);
  assert.equal(values.size, 4);
  const before = [...values];
  applyGaze(model, id => id, gaze, 0);
  assert.deepEqual([...values], before);
});

test('finished motion base smoothly returns to defaults instead of retaining its final pose', () => {
  let value = 20;
  const model = {
    getParameterCount: () => 1,
    getParameterDefaultValue: () => 0,
    getParameterValueByIndex: () => value,
    setParameterValueByIndex: (_, v) => { value = v; }
  };
  restoreMotionBase(model, 1 / 60);
  assert.ok(value > 0 && value < 20);
  for (let i = 0; i < 180; i++) restoreMotionBase(model, 1 / 60);
  assert.ok(value < 0.02);
});

test('head hover waits for dwell, reacts once, releases on leave and respects cooldown', () => {
  const h = new HeadHover(() => 0.5);
  assert.equal(h.update(true, 1, false, 0.016, 0).state, 'dwell');
  assert.equal(h.update(true, 1, false, 0.016, 0.59).triggered, false);
  const reaction = h.update(true, 1, false, 0.016, 0.61);
  assert.equal(reaction.triggered, true);
  assert.ok(reaction.tilt > 0 && reaction.tilt < 3);
  assert.equal(h.update(true, 1, false, 0.016, 0.73).blink, 0);
  assert.equal(h.update(true, 1, false, 0.016, 1).blink, 1);
  assert.equal(h.update(true, 1, false, 0.016, 12).triggered, false);
  h.update(false, 1, false, 0.1, 12.1);
  h.update(true, -1, false, 0.016, 12.2);
  assert.equal(h.update(true, -1, false, 0.1, 12.81).triggered, true);
  assert.ok(h.update(false, -1, false, 0.1, 12.9).tilt < 0);
  for (let i = 0; i < 10; i++) h.update(false, -1, false, 0.1, 13 + i / 10);
  assert.ok(Math.abs(h.tilt) < 0.01);
});

test('leaving before dwell resets it; rapid re-entry and motion cannot trigger reactions', () => {
  const h = new HeadHover(() => 0.5);
  h.update(true, 1, false, 0.1, 0);
  h.update(false, 1, false, 0.1, 0.5);
  h.update(true, 1, false, 0.1, 0.55);
  assert.equal(h.update(true, 1, false, 0.1, 0.7).triggered, false);
  h.update(true, 1, false, 0.1, 1.2);
  const tap = h.update(true, 1, true, 0.1, 1.3);
  assert.equal(tap.tilt, 0);
  assert.equal(tap.blink, 1);
  assert.equal(h.update(true, 1, false, 0.1, 20).triggered, false);
  h.update(false, 1, false, 0.1, 20.1);
  h.update(true, 1, false, 0.1, 20.2);
  assert.equal(h.update(true, 1, false, 0.1, 20.81).triggered, true);
  h.update(false, 1, false, 0.1, 21);
  h.update(true, 1, false, 0.1, 21.1);
  assert.equal(h.update(true, 1, false, 0.1, 22).triggered, false);
});

test('head region uses current descendant triangles and rejects body and mesh holes', () => {
  const core = {
    parts: { ids: ['Part', 'eye', 'body'], parentIndices: [-1, 0, -1] },
    drawables: { parentPartIndices: [1, 2], opacities: [1, 1], indices: [new Uint16Array([0, 1, 2]), new Uint16Array([0, 1, 2])],
      textureIndices: [0, 0], vertexUvs: [new Float32Array([0, 0, 1, 0, 0, 1]), new Float32Array([0, 0, 1, 0, 0, 1])],
      vertexPositions: [new Float32Array([0, 0, 1, 0, 0, 1]), new Float32Array([-1, -1, 0, -1, 0, 0])] }
  };
  const region = new HeadHitRegion(core);
  const matrix = { invertTransformX: x => x, invertTransformY: y => y };
  assert.equal(region.isHit({ x: 0.6, y: 0.4 }, matrix), false);
  region.setTexture(0, { width: 1, height: 1, alpha: new Uint8Array([255]) });
  assert.equal(region.isHit({ x: 0.6, y: 0.4 }, matrix), true);
  assert.equal(region.isHit({ x: 0.9, y: 0.1 }, matrix), false);
  assert.equal(region.isHit({ x: 0.25, y: 0.75 }, matrix), false);
  core.drawables.opacities[0] = 0;
  assert.equal(region.isHit({ x: 0.6, y: 0.4 }, matrix), false);
});

test('transparent pixels in a head mesh cannot trigger hover', () => {
  const region = new HeadHitRegion({ parts: { ids: ['Part'], parentIndices: [-1] }, drawables: {
    parentPartIndices: [0], opacities: [1], textureIndices: [0], indices: [new Uint16Array([0, 1, 2])],
    vertexPositions: [new Float32Array([-1, -1, 1, -1, -1, 1])], vertexUvs: [new Float32Array([0, 0, 1, 0, 0, 1])]
  } });
  region.setTexture(0, { width: 1, height: 2, alpha: new Uint8Array([255, 0]) });
  const matrix = { invertTransformX: x => x, invertTransformY: y => y };
  assert.equal(region.isHit({ x: 0.1, y: 0.8 }, matrix), false);
  assert.equal(region.isHit({ x: 0.1, y: 0.3 }, matrix), true);
});

test('hover blink only multiplies declared eye channels, preserving closed expressions', () => {
  const values = new Map([['left', 0], ['right', 0.8], ['mouth', 0.4]]);
  const model = { getParameterCount: () => values.size, getParameterId: i => [...values.keys()][i],
    multiplyParameterValueById: (id, v) => values.set(id, values.get(id) * v) };
  applyHoverBlink(model, ['left', 'right', 'absent'], 0.5);
  assert.equal(values.get('left'), 0);
  assert.equal(values.get('right'), 0.4);
  assert.equal(values.get('mouth'), 0.4);
  assert.equal(values.size, 3);
});
