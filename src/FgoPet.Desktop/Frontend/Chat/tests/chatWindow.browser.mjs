import assert from "node:assert/strict";
import { createServer } from "node:http";
import { mkdir, readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { fixture } from "./nativeHostFixture.ts";

// An offline DOM/bridge fixture. Viewport changes emulate host resize receipts;
// actual HWND placement, DPI and WebView2 lifecycle require the Windows suite.
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.FGO_PLAYWRIGHT_MODULE || "playwright");
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../dist");
const server = createServer(async (request, response) => {
  try {
    const pathname = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    const relative = pathname === "/" ? "index.html" : pathname.slice(1);
    const file = path.resolve(root, relative);
    if (!file.startsWith(root + path.sep)) throw new Error("Invalid path");
    const bytes = await readFile(file);
    const type = { ".html": "text/html", ".js": "application/javascript", ".css": "text/css", ".woff2": "font/woff2" }[path.extname(file)];
    response.writeHead(200, { "Content-Type": type || "application/octet-stream" });
    response.end(bytes);
  } catch { response.writeHead(404); response.end(); }
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const browser = await chromium.launch({ headless: true,
  ...(process.env.FGO_CHROMIUM_EXECUTABLE ? { executablePath: process.env.FGO_CHROMIUM_EXECUTABLE } : {}),
  args: ["--no-sandbox"],
});
const page = await browser.newPage({ viewport: { width: 440, height: 160 }, reducedMotion: "reduce" });
const artifactDirectory = process.env.FGO_CHAT_ARTIFACT_DIR;
if (artifactDirectory) await mkdir(artifactDirectory, { recursive: true });
const stages = [];
async function capture(name) {
  // Let the host receipt and React's layout effects settle before recording
  // geometry; the screenshot should describe the same rendered frame.
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const stage = await page.evaluate(() => ({
    height: innerHeight,
    view: document.querySelector(".chat-anchor")?.dataset.view,
    messageHeight: document.querySelector(".message-scroll")?.clientHeight,
  }));
  stages.push({ name, ...stage });
  if (artifactDirectory) await page.screenshot({ path: path.join(artifactDirectory, `${name}.png`) });
  return stage;
}
const errors = [];
page.on("pageerror", error => errors.push(error.message));
await page.exposeFunction("resizeFixture", async presentation => {
  await page.setViewportSize({
    width: presentation.view === "conversation" && presentation.wide ? 760 : 440,
    height: presentation.view === "composer" ? presentation.composerHeight : presentation.wide ? 718 : 608,
  });
});
await page.addInitScript(snapshot => {
  const state = structuredClone(snapshot);
  const listeners = new Set();
  const posts = [];
  let holdResize = false;
  let held;
  const emit = data => listeners.forEach(listener => listener({ data: structuredClone(data) }));
  const publish = () => { state.version++; emit({ type: "chat.snapshot", payload: state }); };
  const reply = request => emit({ type: "command.result", requestId: request.requestId, success: true,
    payload: { snapshot: state } });
  const complete = async request => {
    const payload = request.payload || {};
    if (request.type === "chat.draft") {
      state.draft = { text: payload.text, revision: payload.revision };
      state.conversation.canSend = !state.conversation.isStreaming && !!payload.text.trim();
    } else if (request.type === "chat.send") {
      const template = state.conversation.turns[0];
      state.conversation.turns.push({ ...template, id: "user-current", role: "user", text: state.draft.text,
        canCopy: false, canReadAloud: false }, { ...template, id: "assistant-current", text: "", isStreaming: true });
      state.draft.text = "";
      state.conversation.isStreaming = true;
      state.conversation.isThinking = true;
      state.conversation.canSend = false;
      state.conversation.canStop = true;
    } else if (request.type === "chat.host" && payload.action === "setPresentation") {
      state.presentation.window = { view: payload.view, wide: payload.wide,
        composerHeight: payload.composerHeight ?? state.presentation.window.composerHeight };
      await window.resizeFixture(state.presentation.window);
    } else if (request.type === "chat.host" && payload.action === "hide") {
      emit({ type: "chat.visibility", visible: false });
    }
    publish(); reply(request);
  };
  window.chrome = { ...window.chrome, webview: {
    addEventListener: (_, listener) => listeners.add(listener),
    removeEventListener: (_, listener) => listeners.delete(listener),
    postMessage(request) {
      posts.push({ ...structuredClone(request), at: Date.now() });
      if (request.type === "ready") { publish(); return; }
      if (request.type === "theme.ack") return;
      if (holdResize && request.type === "chat.host" && request.payload?.composerHeight !== undefined) {
        holdResize = false; held = request; return;
      }
      void complete(request);
    },
  } };
  window.chatFixture = {
    state, posts,
    append(text) {
      const turn = state.conversation.turns.find(turn => turn.id === "assistant-current");
      turn.text += text;
      state.conversation.isThinking = false;
      publish();
    },
    finish() {
      state.conversation.isStreaming = false;
      state.conversation.canStop = false;
      state.conversation.turns.forEach(turn => { turn.isStreaming = false; });
      publish();
    },
    show() { emit({ type: "chat.visibility", visible: true }); },
    holdNextResize() { holdResize = true; },
    hasHeld() { return !!held; },
    async release(errorCode) {
      const request = held; held = undefined;
      if (errorCode) emit({ type: "command.result", requestId: request.requestId, success: false, errorCode });
      else await complete(request);
    },
  };
}, fixture());

try {
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.locator("#composer").waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(150);
  const initial = await capture("chat-01-input");
  const before = await page.evaluate(() => chatFixture.posts.length);
  await page.locator("#composer").fill("你好");
  await page.locator("#composer").press("Enter");
  await page.waitForSelector(".compact-conversation");
  assert.equal(await page.locator(".chat-anchor").getAttribute("data-view"), "composer");
  assert.equal(await page.locator("#messages").textContent().then(text => text.includes("A reply")), false);
  await page.waitForTimeout(150);
  const waiting = await capture("chat-02-waiting");
  assert.equal(await page.locator('[data-message-id="assistant-current"]').count(), 0,
    "an empty assistant placeholder must not duplicate the waiting indicator");
  await page.evaluate(() => chatFixture.append("你好，我在。"));
  await page.waitForTimeout(150);
  const shortReply = await capture("chat-03-short-reply");
  assert.equal(shortReply.view, "composer");
  assert.ok(shortReply.height > initial.height && shortReply.height < 320,
    `a short reply should grow the compact window without immediately using its full cap: ${JSON.stringify({initial, shortReply})}`);
  assert.ok(waiting.height < 320, "waiting must leave room for the reply to grow");
  assert.ok(waiting.height <= shortReply.height, "the single waiting row must not pre-expand past a short reply");
  for (let i = 0; i < 20; i++) {
    await page.evaluate(i => chatFixture.append(`Line ${i}: a little more context.\n\n`), i);
    await page.waitForTimeout(25);
  }
  await page.waitForFunction(() => innerHeight === 360);
  const geometry = await page.evaluate(() => {
    const scroll = document.querySelector(".message-scroll");
    return { height: innerHeight, client: scroll.clientHeight, content: scroll.scrollHeight,
      composerBottom: document.querySelector(".composer").getBoundingClientRect().bottom,
      surfaceTop: document.querySelector(".chat-surface").getBoundingClientRect().top,
      horizontalOverflow: document.documentElement.scrollWidth > innerWidth };
  });
  assert.ok(geometry.content > geometry.client);
  assert.ok(geometry.client > 20 && geometry.composerBottom <= geometry.height + 1 && geometry.surfaceTop >= -1);
  assert.equal(geometry.horizontalOverflow, false);
  const streamSizes = await page.evaluate(before => chatFixture.posts.slice(before).filter(item => item.payload?.composerHeight !== undefined), before);
  assert.ok(streamSizes.length >= 2, "height must update during streaming");
  assert.equal(await page.evaluate(() => chatFixture.posts.some(item => item.payload?.view === "conversation")), false);
  await capture("chat-04-height-cap");

  await page.locator("#composer").fill("Unsent follow-up stays here.");
  await page.evaluate(() => {
    const scroll = document.querySelector(".message-scroll");
    scroll.dispatchEvent(new WheelEvent("wheel", { deltaY: -100, bubbles: true }));
    scroll.scrollTop = 0;
    scroll.dispatchEvent(new Event("scroll"));
  });
  await page.evaluate(() => chatFixture.append("A later streamed paragraph must not move this reading position.\n\n"));
  await page.waitForTimeout(100);
  assert.ok(await page.locator(".message-scroll").evaluate(el => el.scrollTop < 2));
  await page.evaluate(() => {
    const text = document.querySelector('[data-message-id="assistant-current"] p').firstChild;
    const range = document.createRange(); range.setStart(text, 0); range.setEnd(text, Math.min(6, text.length));
    window.getSelection().removeAllRanges(); window.getSelection().addRange(range);
  });
  const selected = await page.evaluate(() => window.getSelection().toString());
  await page.evaluate(() => chatFixture.append("Selection should remain readable.\n\n"));
  await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => window.getSelection().toString()), selected);
  assert.ok(await page.locator(".message-scroll").evaluate(el => el.scrollTop < 2));
  await page.evaluate(() => {
    chatFixture.state.conversation.turns.find(turn => turn.id === "user-current").canCopy = true;
    chatFixture.append("");
  });
  await page.getByRole("button", { name: "展开阅读", exact: true }).click();
  await page.waitForFunction(() => document.querySelector(".chat-anchor").dataset.view === "conversation" && innerHeight > 360);
  assert.equal(await page.locator("#composer").inputValue(), "Unsent follow-up stays here.");
  const userCopyOutside = await page.locator('[data-message-id="user-current"]').evaluate(el => {
    const text=el.querySelector('.message-text').getBoundingClientRect();
    const actions=el.querySelector('.message-actions').getBoundingClientRect();
    return actions.top >= text.bottom && actions.right <= el.getBoundingClientRect().right + 1;
  });
  assert.equal(userCopyOutside, true, "user copy actions stay below the sent bubble");
  await capture("chat-05-full-conversation");
  await page.locator(".chat-return").click();
  await page.waitForSelector(".compact-conversation");
  await page.waitForFunction(() => innerHeight === 360);
  await capture("chat-06-return-to-turn");
  await page.getByRole("button", { name: "隐藏聊天窗口", exact: true }).click();
  await page.evaluate(() => chatFixture.show());
  assert.equal(await page.locator("#composer").inputValue(), "Unsent follow-up stays here.");
  assert.equal(await page.locator(".chat-anchor").getAttribute("data-view"), "composer");

  await page.evaluate(() => chatFixture.holdNextResize());
  await page.getByRole("button", { name: "收成输入框", exact: true }).click();
  await page.waitForFunction(() => chatFixture.hasHeld());
  await page.getByRole("button", { name: "展开完整对话", exact: true }).click();
  await page.waitForFunction(() => document.querySelector(".chat-anchor").dataset.view === "conversation");
  await page.evaluate(() => chatFixture.release());
  await page.waitForFunction(() => chatFixture.state.presentation.window.view === "conversation");
  await page.waitForTimeout(150);
  assert.equal(await page.locator(".chat-anchor").getAttribute("data-view"), "conversation");
  assert.equal(await page.locator("#composer").inputValue(), "Unsent follow-up stays here.");
  await page.evaluate(() => chatFixture.holdNextResize());
  await page.locator(".chat-return").click();
  await page.waitForFunction(() => chatFixture.hasHeld());
  await page.evaluate(() => {
    chatFixture.state.identity.conversationId = "conversation-after-admission";
    chatFixture.append("");
  });
  await page.evaluate(() => chatFixture.release("CHAT_IDENTITY_STALE"));
  await page.waitForTimeout(150);
  assert.equal(await page.locator(".error-card").count(), 0, "an obsolete window resize must not show a failed-turn card during generation");
  assert.equal(await page.evaluate(() => chatFixture.state.conversation.isStreaming), true);
  await page.evaluate(() => chatFixture.finish());
  await page.waitForTimeout(150);
  await page.getByRole("button", { name: "展开完整对话", exact: true }).click();
  await page.waitForFunction(() => document.querySelector(".chat-anchor").dataset.view === "conversation");
  assert.equal(await page.locator(".error-card").count(), 0, "normal completion must remain free of stale window errors");
  assert.ok((await page.locator('[data-message-id="assistant-current"]').textContent()).includes("Line 19"),
    "the completed response remains readable after the obsolete resize is rejected");
  await capture("chat-07-completed-without-stale-error");
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: true, checks: ["stream grows without mode switch", "short reply stays compact", "bounded internal scroll", "user scroll and selection", "draft through expand/collapse/hide", "late resize cannot undo explicit expansion", "stale window error cannot mark generation or completion as failed"], stages, geometry, streamHeightReports: streamSizes.map(item => item.payload.composerHeight) }));
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
