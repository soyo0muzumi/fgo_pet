// Application-owned state priority; role packs supply only named motion data.
const expressions = new Set(['neutral', 'happy', 'excited', 'shy', 'concerned', 'sad', 'surprised', 'angry', 'wants_to_talk']);
const groups = { thinking: 'Thinking', shy: 'Shy', concerned: 'Concerned', surprised: 'Surprised', sleep: 'Sleep' };

export class DesktopState {
  constructor(sleepAfter = 600) {
    this.sleepAfter = sleepAfter;
    this.lastActiveAt = 0;
    this.expression = 'neutral';
    this.thinking = false;
    this.sleeping = false;
    this.state = 'neutral';
    this.pose = null;
    this.hasMotion = false;
  }

  receive(message, now) {
    if (!Number.isFinite(now)) return false;
    if (message?.type === 'live2d.expression' && expressions.has(message.key)) {
      this.expression = message.key;
      if (message.key !== 'neutral') this.sleeping = false;
    } else if (message?.type === 'live2d.thinking' && typeof message.active === 'boolean') {
      this.thinking = message.active;
      if (this.thinking) this.sleeping = false;
    } else return false;
    this.lastActiveAt = now;
    return true;
  }

  activity(now) { if (!this.sleeping && Number.isFinite(now)) this.lastActiveAt = now; }

  speech(active, now) {
    if (!active || !Number.isFinite(now)) return;
    this.lastActiveAt = now;
    if (this.sleeping) { this.sleeping = false; this.expression = 'neutral'; }
  }

  tap(now) {
    if (!Number.isFinite(now)) return false;
    this.lastActiveAt = now;
    if (this.thinking) return false;
    const wasSleeping = this.sleeping;
    this.sleeping = false;
    this.expression = 'neutral';
    return !wasSleeping;
  }

  sync(owner, now) {
    if (!this.thinking && !this.sleeping && now - this.lastActiveAt >= this.sleepAfter) this.sleeping = true;
    const expression = this.expression === 'sad' ? 'concerned' : this.expression;
    const next = this.thinking ? 'thinking' : this.sleeping ? 'sleep' : expression;
    if (next === this.state) {
      // Short emotions yield to the runtime's gradual default-parameter recovery.
      // Thinking and Sleep keep their pose until an explicit state transition.
      if (this.hasMotion && next !== 'thinking' && next !== 'sleep' && owner._motionManager.isFinished()) {
        this.expression = this.state = 'neutral';
        this.pose = null;
        this.hasMotion = false;
      }
      return;
    }
    owner._motionManager.stopAllMotions();
    this.state = next;
    this.pose = null;
    const group = groups[next];
    const motion = group && owner._motions.get(group + '_0');
    this.hasMotion = !!motion && owner._modelSetting.getMotionCount(group) > 0;
    if (this.hasMotion) {
      motion.setLoop(false);
      owner.startMotion(group, 0, 3);
    }
  }

  get blocksGaze() { return this.thinking || this.sleeping || this.hasMotion; }
  get blocksBlink() { return this.state === 'sleep'; }

  capture(model) {
    if (!this.hasMotion) return;
    this.pose = Array.from({ length: model.getParameterCount() }, (_, i) => model.getParameterValueByIndex(i));
  }

  applyBase(model) {
    if (!this.hasMotion || !this.pose) return false;
    this.pose.forEach((value, i) => model.setParameterValueByIndex(i, value));
    return true;
  }

  applyEffects(model, idFor, motionFinished) {
    if (!motionFinished) return;
    const core = model.getModel();
    const set = (name, value) => {
      if (core.parameters.ids.includes(name)) model.setParameterValueById(idFor(name), value);
    };
    if (this.blocksBlink) {
      set('ParamEyeLOpen', 0); set('ParamEyeROpen', 0);
      if (!this.hasMotion) set('ParamAngleY', -15);
    } else if (this.hasMotion && this.pose) {
      for (const name of ['ParamEyeLOpen', 'ParamEyeROpen']) {
        const i = core.parameters.ids.indexOf(name);
        if (i >= 0) model.multiplyParameterValueById(idFor(name), Math.min(1, Math.max(0, this.pose[i])));
      }
    } else if (this.state === 'happy' || this.state === 'excited') {
      set('ParamMouthForm', this.state === 'happy' ? 0.45 : 0.8);
      set('ParamEyeLSmile', 0.15); set('ParamEyeRSmile', 0.15);
    }
  }
}

export const desktopState = new DesktopState();
