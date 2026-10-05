// Receives energy only. Audio decoding and playback remain owned by Speech.
export class SpeechLipSync {
  constructor() { this.playing = false; this.level = 0; this.receivedAt = -Infinity; this.open = 0; this.weight = 0; }
  receive(message, now) {
    if (message?.type !== 'live2d.speech' || typeof message.active !== 'boolean'
      || !Number.isFinite(message.level) || message.level < 0 || message.level > 1
      || !Number.isFinite(now)) return false;
    this.playing = message.active;
    this.level = message.active ? message.level : 0;
    this.receivedAt = now;
    return true;
  }
  active(now) { return this.playing && now >= this.receivedAt && now - this.receivedAt <= .5; }
  update(dt, now) {
    const active = this.active(now);
    const target = active ? Math.max(0, this.level - .04) / .96 * .55 : 0;
    const step = Number.isFinite(dt) ? Math.max(0, Math.min(1, dt)) : 0;
    this.open += (target - this.open) * (1 - Math.exp(-step / (target > this.open ? .045 : .065)));
    this.weight += ((active ? 1 : 0) - this.weight) * (1 - Math.exp(-step / (active ? .04 : .12)));
    return { open: this.open, weight: this.weight };
  }
  apply(model, idFor, frame) {
    if (frame.weight < .001 || !model.getModel().parameters.ids.includes('ParamMouthOpenY')) return;
    model.setParameterValueById(idFor('ParamMouthOpenY'), frame.open, frame.weight);
  }
}
export const desktopSpeech = new SpeechLipSync();
