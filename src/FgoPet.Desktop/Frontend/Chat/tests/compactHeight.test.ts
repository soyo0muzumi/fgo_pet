import assert from "node:assert/strict";
import test from "node:test";
import { compactHeight, createCompactHeightReporter } from "../src/compactHeight.ts";

const settled = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

test("continuous stream growth is reported before the stream becomes quiet", async t => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"], now: 0 });
  const sent: Array<{ height: number; at: number }> = [];
  const reporter = createCompactHeightReporter(async height => { sent.push({ height, at: Date.now() }); }, error => assert.fail(String(error)));
  t.after(() => reporter.dispose());
  reporter.measure(160);
  await settled();
  for (let time = 10; time <= 240; time += 10) {
    t.mock.timers.tick(10);
    reporter.measure(160 + time / 2);
    await settled();
  }
  assert.ok(sent.length >= 4, "ongoing 10ms updates must not starve the bridge");
  assert.ok(sent.every((value, i) => i === 0 || value.at - sent[i - 1].at >= 60));
  t.mock.timers.tick(60);
  await settled();
  assert.equal(sent.at(-1)?.height, 280);
});

test("a slow host receives only the latest height after its current call completes", async t => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"], now: 0 });
  const sent: number[] = [];
  let release!: () => void;
  const reporter = createCompactHeightReporter(height => {
    sent.push(height);
    return sent.length === 1 ? new Promise<void>(resolve => { release = resolve; }) : Promise.resolve();
  }, error => assert.fail(String(error)));
  t.after(() => reporter.dispose());
  reporter.measure(180);
  for (const height of [190, 210, 250, 320, 1000]) reporter.measure(height);
  t.mock.timers.tick(500);
  assert.deepEqual(sent, [180]);
  release();
  await settled();
  assert.deepEqual(sent, [180, 360]);
});

test("a failed height can retry on a later measurement without an automatic retry loop", async t => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"], now: 0 });
  const sent: number[] = [];
  const errors: unknown[] = [];
  const failure = new Error("temporary host failure");
  let reject!: (reason: unknown) => void;
  const reporter = createCompactHeightReporter(height => {
    sent.push(height);
    return sent.length === 1
      ? new Promise<void>((_, fail) => { reject = fail; }) : Promise.resolve();
  }, error => errors.push(error));
  t.after(() => reporter.dispose());
  reporter.measure(240);
  reporter.measure(240);
  reject(failure);
  await settled();
  t.mock.timers.tick(1000);
  await settled();
  assert.deepEqual(errors, [failure]);
  assert.deepEqual(sent, [240], "failure alone must not repeatedly call the host");
  reporter.measure(240);
  await settled();
  assert.deepEqual(sent, [240, 240], "a later measurement can recover the rejected size");
});

test("explicit mode changes cancel both a timer and an in-flight trailing resize", async t => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"], now: 0 });
  const sent: number[] = [];
  let release!: () => void;
  const reporter = createCompactHeightReporter(height => {
    sent.push(height);
    return new Promise<void>(resolve => { release = resolve; });
  }, error => assert.fail(String(error)));
  reporter.measure(180);
  reporter.measure(330);
  reporter.dispose();
  release();
  await settled();
  t.mock.timers.tick(1000);
  reporter.measure(360);
  assert.deepEqual(sent, [180]);

  const second = createCompactHeightReporter(async height => { sent.push(height); }, error => assert.fail(String(error)));
  second.measure(200);
  await settled();
  second.measure(240);
  second.dispose();
  t.mock.timers.tick(1000);
  assert.deepEqual(sent, [180, 200]);
});

test("height reports are finite, bounded and do not repeat equivalent sizes", async t => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"], now: 0 });
  assert.equal(compactHeight(Number.NaN), 160);
  assert.equal(compactHeight(Number.POSITIVE_INFINITY), 160);
  assert.equal(compactHeight(175.2), 176);
  const sent: number[] = [];
  const reporter = createCompactHeightReporter(async height => { sent.push(height); }, error => assert.fail(String(error)));
  t.after(() => reporter.dispose());
  reporter.measure(600);
  await settled();
  for (let i = 0; i < 10; i++) {
    t.mock.timers.tick(70);
    reporter.measure(1000 + i);
    await settled();
  }
  assert.deepEqual(sent, [360]);
});
