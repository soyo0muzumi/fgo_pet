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
    this.phase = 'idle';
    this.sequence = -1;
    this.phaseAt = 0;
    this.frameAt = null;
    this.feedbackHeadX = this.feedbackHeadY = this.feedbackHeadZ = this.feedbackBodyX = 0;
    this.feedbackEyeX = this.feedbackEyeY = this.feedbackSmile = this.feedbackMouthForm = 0;
  }

  receive(message, now) {
    if (!Number.isFinite(now)) return false;
    if (message?.type === 'live2d.expression' && expressions.has(message.key)) {
      this.expression = message.key;
      if (message.key !== 'neutral') this.sleeping = false;
    } else if (message?.type === 'live2d.thinking' && typeof message.active === 'boolean') {
      if (message.phase !== undefined) {
        if (!['idle', 'waiting', 'replying'].includes(message.phase)
          || message.active !== (message.phase === 'waiting')
          || !Number.isSafeInteger(message.sequence) || message.sequence < 0
          || message.sequence < this.sequence) return false;
        if (message.sequence === this.sequence) return message.phase === this.phase;
        this.sequence = message.sequence;
        if (message.phase !== this.phase) this.phaseAt = now;
        this.phase = message.phase;
      } else {
        // Once a sequenced host is connected, unsequenced replay cannot revive it.
        if (this.sequence >= 0) return false;
        this.phase = 'idle';
      }
      this.thinking = message.active;
      if (this.thinking || this.phase === 'replying') this.sleeping = false;
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
    if (this.thinking || this.phase === 'replying') return false;
    const wasSleeping = this.sleeping;
    this.sleeping = false;
    this.expression = 'neutral';
    return !wasSleeping;
  }

  sync(owner, now) {
    const dt = this.frameAt === null ? 0 : Math.min(.1, Math.max(0, now - this.frameAt));
    this.frameAt = now;
    const age = Math.max(0, now - this.phaseAt);
    // The reply nod runs once, independent of token rate. All offsets are transient:
    // no hand or mouth-opening channels; nothing is saved into the motion base.
    // Head/eye directions follow the supplied Thinking pose at a bounded scale.
    // The return to the viewer and one nod distinguish text replies at pet size.
    const replying = this.phase === 'replying';
    const nod = replying && age < 1.8 ? -14 * Math.sin(Math.PI * age / 1.8) : 0;
    const waiting = this.phase === 'waiting';
    const weight = 1 - Math.exp(-dt / .3);
    this.feedbackHeadX += ((waiting ? 20 : 0) - this.feedbackHeadX) * weight;
    this.feedbackHeadY += ((waiting ? 13 + .8 * Math.sin(age * .65) : replying ? 2 + nod : 0) - this.feedbackHeadY) * weight;
    this.feedbackHeadZ += ((waiting ? -16 : replying ? 3 : 0) - this.feedbackHeadZ) * weight;
    this.feedbackBodyX += ((waiting ? 2 : 0) - this.feedbackBodyX) * weight;
    this.feedbackEyeX += ((waiting ? .35 : 0) - this.feedbackEyeX) * weight;
    this.feedbackEyeY += ((waiting ? .65 : 0) - this.feedbackEyeY) * weight;
    this.feedbackSmile += ((replying ? .45 : 0) - this.feedbackSmile) * weight;
    this.feedbackMouthForm += ((waiting ? -.25 : replying ? .55 : 0) - this.feedbackMouthForm) * weight;
    if (!this.thinking && this.phase === 'idle' && !this.sleeping && now - this.lastActiveAt >= this.sleepAfter) this.sleeping = true;
    const expression = this.expression === 'sad' ? 'concerned' : this.expression;
    const next = this.phase !== 'idle' ? this.phase : this.thinking ? 'thinking' : this.sleeping ? 'sleep' : expression;
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

  get blocksGaze() { return this.thinking || this.sleeping || this.hasMotion || Math.abs(this.feedbackHeadX) + Math.abs(this.feedbackHeadY) + Math.abs(this.feedbackHeadZ) > .05; }
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
    for (const [name, value] of [['ParamAngleX', this.feedbackHeadX], ['ParamAngleY', this.feedbackHeadY], ['ParamAngleZ', this.feedbackHeadZ], ['ParamBodyAngleX', this.feedbackBodyX],
      ['ParamEyeBallX', this.feedbackEyeX], ['ParamEyeBallY', this.feedbackEyeY], ['ParamEyeLSmile', this.feedbackSmile], ['ParamEyeRSmile', this.feedbackSmile], ['ParamMouthForm', this.feedbackMouthForm]]) {
      if (Math.abs(value) > .0001 && core.parameters.ids.includes(name)) model.addParameterValueById(idFor(name), value);
    }
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
