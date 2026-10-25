// App-owned procedural behavior; role packs remain data-only.
const clamp = (v, lo = -1, hi = 1) => Math.max(lo, Math.min(hi, v));
const ease = (value, target, dt, tau) => value + (target - value) * (1 - Math.exp(-dt / tau));
const deadZone = (value, radius) => Math.sign(value) * Math.max(0, (Math.abs(clamp(value)) - radius) / (1 - radius));

export class DesktopBehavior {
  constructor(random = Math.random) {
    this.random = random;
    this.pointer = null;
    this.receivedAt = -Infinity;
    this.attendedAt = -Infinity;
    this.near = false;
    this.nextIdleAt = 0;
    this.idleX = this.idleY = 0;
    this.eyeX = this.eyeY = this.headX = this.headY = 0;
  }

  receive(message, now) {
    if (message?.type !== 'live2d.pointer' || !Number.isFinite(message.x) || !Number.isFinite(message.y)
      || typeof message.active !== 'boolean' || !Number.isFinite(now)) return false;
    this.pointer = { x: clamp(message.x, -100, 100), y: clamp(message.y, -100, 100) };
    this.receivedAt = now;
    if (message.active) this.interact(now);
    return true;
  }

  interact(now) { if (Number.isFinite(now)) this.attendedAt = now; }

  update(delta, now, origin) {
    const dt = Number.isFinite(delta) ? clamp(delta, 0, 0.1) : 0;
    const fresh = this.pointer && now - this.receivedAt <= 0.5;
    const near = !!fresh && Math.hypot((this.pointer.x - origin.x) / 0.25, (this.pointer.y - origin.y) / 0.15) <= 1;
    if (near && !this.near) this.interact(now);
    this.near = near;
    const age = now - this.attendedAt;
    const mode = !fresh || age >= 8 ? 'idle' : age >= 4 ? 'soft' : 'follow';
    if (now >= this.nextIdleAt) {
      this.idleX = (this.random() * 2 - 1) * 0.35;
      this.idleY = (this.random() * 2 - 1) * 0.2;
      this.nextIdleAt = now + 6 + this.random() * 12;
    }
    const attention = mode === 'follow' ? 1 : mode === 'soft' ? (8 - age) / 4 : 0;
    const pointerX = fresh ? deadZone((this.pointer.x - origin.x) / 0.85, 0.04) : 0;
    const pointerY = fresh ? deadZone((origin.y - this.pointer.y) / 0.65, 0.04) : 0;
    const x = this.idleX * (1 - attention) + pointerX * attention;
    const y = this.idleY * (1 - attention) + pointerY * attention;
    this.eyeX = ease(this.eyeX, x, dt, 0.12);
    this.eyeY = ease(this.eyeY, y, dt, 0.12);
    this.headX = ease(this.headX, deadZone(x, 0.18), dt, 0.55);
    this.headY = ease(this.headY, deadZone(y, 0.18), dt, 0.55);
    return { mode, eyeX: this.eyeX, eyeY: this.eyeY, headX: this.headX, headY: this.headY };
  }
}

export const desktopBehavior = new DesktopBehavior();

export class HeadHover {
  constructor(random = Math.random) {
    this.random = random;
    this.enteredAt = null;
    this.reactedAt = -Infinity;
    this.cooldownUntil = -Infinity;
    this.consumed = false;
    this.direction = 1;
    this.tilt = 0;
  }

  update(inside, direction, motionPlaying, delta, now) {
    const dt = Number.isFinite(delta) ? clamp(delta, 0, 0.1) : 0;
    let triggered = false;
    if (!inside) { this.enteredAt = null; this.consumed = false; }
    else if (this.enteredAt === null) this.enteredAt = now;
    if (motionPlaying) {
      this.consumed = inside;
      this.reactedAt = -Infinity;
      this.tilt = 0;
      return { state: 'suppressed', triggered, tilt: 0, blink: 1 };
    }
    if (inside && !this.consumed && now >= this.cooldownUntil && now - this.enteredAt >= 0.6) {
      this.consumed = true;
      this.reactedAt = now;
      this.cooldownUntil = now + 5 + this.random() * 3;
      this.direction = direction < 0 ? -1 : 1;
      triggered = true;
    }
    const age = now - this.reactedAt;
    const reacting = inside && age < 0.7;
    this.tilt = ease(this.tilt, reacting ? this.direction * 3 : 0, dt, reacting ? 0.15 : 0.13);
    const blink = age < 0.1 ? 1 - age / 0.1 : age < 0.15 ? 0 : age < 0.3 ? (age - 0.15) / 0.15 : 1;
    const state = reacting ? 'reaction' : !inside ? 'outside' : this.consumed || now < this.cooldownUntil ? 'cooldown' : 'dwell';
    return { state, triggered, tilt: this.tilt, blink: clamp(blink, 0, 1) };
  }
}

// Mash head mesh selection is app-owned. Cache membership, but use current vertices
// so hovering remains aligned when gaze/motion deforms the face.
export class HeadHitRegion {
  constructor(core) {
    this.core = core;
    this.indices = [];
    this.textures = new Map();
    const head = core.parts.ids.indexOf('Part');
    if (head < 0) return;
    for (let i = 0; i < core.drawables.parentPartIndices.length; i++) {
      let parent = core.drawables.parentPartIndices[i], remaining = core.parts.ids.length;
      while (parent >= 0 && parent !== head && remaining-- > 0) parent = core.parts.parentIndices[parent];
      if (parent === head) this.indices.push(i);
    }
  }

  setTexture(index, mask) { this.textures.set(index, mask); }

  isHit(pointer, matrix) {
    if (!pointer || !matrix) return false;
    const x = matrix.invertTransformX(pointer.x * 2 - 1);
    const y = matrix.invertTransformY(1 - pointer.y * 2);
    if (!Number.isFinite(x) || !Number.isFinite(y)) return false;
    const drawables = this.core.drawables;
    for (const i of this.indices) {
      if (drawables.opacities[i] <= 0.1) continue;
      const positions = drawables.vertexPositions[i], triangles = drawables.indices[i];
      for (let j = 0; j < triangles.length; j += 3) {
        const a = triangles[j] * 2, b = triangles[j + 1] * 2, c = triangles[j + 2] * 2;
        const ax = positions[a], ay = positions[a + 1], bx = positions[b], by = positions[b + 1], cx = positions[c], cy = positions[c + 1];
        const area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.abs(area) < 1e-10) continue;
        const u = ((bx - x) * (cy - y) - (by - y) * (cx - x)) / area;
        const v = ((cx - x) * (ay - y) - (cy - y) * (ax - x)) / area;
        if (u >= 0 && v >= 0 && u + v <= 1) {
          const mask = this.textures.get(drawables.textureIndices[i]);
          if (!mask) continue;
          const uv = drawables.vertexUvs[i], w = 1 - u - v;
          const textureX = uv[a] * u + uv[b] * v + uv[c] * w;
          const textureY = 1 - (uv[a + 1] * u + uv[b + 1] * v + uv[c + 1] * w);
          const px = clamp(Math.floor(textureX * mask.width), 0, mask.width - 1);
          const py = clamp(Math.floor(textureY * mask.height), 0, mask.height - 1);
          if (mask.alpha[py * mask.width + px] >= 32) return true;
        }
      }
    }
    return false;
  }
}

export function textureAlphaFromImage(image) {
  const canvas = document.createElement('canvas');
  canvas.width = Math.min(1024, image.width);
  canvas.height = Math.min(1024, image.height);
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  const rgba = context.getImageData(0, 0, canvas.width, canvas.height).data;
  const alpha = new Uint8Array(canvas.width * canvas.height);
  for (let i = 0; i < alpha.length; i++) alpha[i] = rgba[i * 4 + 3];
  return { width: canvas.width, height: canvas.height, alpha };
}

export function applyHoverBlink(model, blinkIds, openness) {
  if (openness >= 1) return;
  const ids = new Set();
  for (let i = 0; i < model.getParameterCount(); i++) ids.add(model.getParameterId(i));
  for (const id of blinkIds) if (ids.has(id)) model.multiplyParameterValueById(id, clamp(openness, 0, 1));
}

// Neutral geometry avoids a feedback loop as the face turns toward the pointer.
// Mash's eye part IDs come from its export, not from executable pack content.
export function eyeOriginFromModel(core) {
  const { parts, drawables } = core;
  const centers = ['Part5', 'Part6'].map(id => {
    const part = parts.ids.indexOf(id);
    if (part < 0) return null;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (let i = 0; i < drawables.parentPartIndices.length; i++) {
      let parent = drawables.parentPartIndices[i];
      let remaining = parts.ids.length;
      while (parent >= 0 && parent !== part && remaining-- > 0) parent = parts.parentIndices[parent];
      if (parent !== part) continue;
      const vertices = drawables.vertexPositions[i];
      for (let j = 0; j < vertices.length; j += 2) {
        minX = Math.min(minX, vertices[j]); maxX = Math.max(maxX, vertices[j]);
        minY = Math.min(minY, vertices[j + 1]); maxY = Math.max(maxY, vertices[j + 1]);
      }
    }
    return Number.isFinite(minX) ? { x: (minX + maxX) / 2, y: (minY + maxY) / 2 } : null;
  });
  if (centers.some(center => !center)) return null;
  return { x: (centers[0].x + centers[1].x) / 2, y: (centers[0].y + centers[1].y) / 2 };
}

export function projectEyeOrigin(eye, matrix) {
  return { x: (matrix.transformX(eye.x) + 1) / 2, y: (1 - matrix.transformY(eye.y)) / 2 };
}

// Runs before saveParameters/effects. Expression, blink, look, breath and physics
// remain transient layers and are never saved into this motion base.
export function restoreMotionBase(model, delta) {
  const dt = Number.isFinite(delta) ? clamp(delta, 0, 0.1) : 0;
  for (let i = 0; i < model.getParameterCount(); i++) {
    model.setParameterValueByIndex(i, ease(model.getParameterValueByIndex(i), model.getParameterDefaultValue(i), dt, 0.4));
  }
}

export function applyGaze(model, idFor, gaze, weight) {
  if (weight <= 0) return;
  const ids = new Set();
  for (let i = 0; i < model.getParameterCount(); i++) ids.add(model.getParameterId(i));
  const set = (name, value) => {
    const id = idFor(name);
    if (ids.has(id)) model.setParameterValueById(id, value, weight);
  };
  const add = (name, value) => {
    const id = idFor(name);
    if (ids.has(id)) model.addParameterValueById(id, value * weight);
  };
  set('ParamEyeBallX', gaze.eyeX);
  set('ParamEyeBallY', gaze.eyeY);
  add('ParamAngleX', gaze.headX * 12);
  add('ParamAngleY', gaze.headY * 10);
}
