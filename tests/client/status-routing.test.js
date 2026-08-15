"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

test("a 404 on the timings probe removes the element entirely", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 404, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "missing");
  el.connectedCallback();

  await el._toggle();

  assert.equal(el._removed, true);
  assert.equal(harness.audioInstances.length, 0);
  assert.equal(harness.speech.calls.speak.length, 0);
});

test("a 429 marks the element throttled, keeps the button usable, and lets a retry succeed", async () => {
  let status = 429;
  const harness = load({
    fetch: async (url) => {
      if (status === 429) return { ok: false, status: 429, url, json: async () => ({}) };
      return { ok: true, status: 200, url, json: async () => [] };
    },
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle();

  assert.equal(el.dataset.state, "throttled");
  assert.equal(el._button.disabled, false);
  assert.equal(harness.speech.calls.speak.length, 0, "a rate limit must not degrade to speech");
  assert.notEqual(el._removed, true);

  status = 200;
  await el._toggle();

  assert.notEqual(el.dataset.state, "throttled", "a later success clears the marker");
  assert.equal(harness.audioInstances.length, 1);
});

test("a 503 degrades to speech synthesis and marks the element degraded", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#article");
  harness.registry["#article"] = harness.createTarget("Hello there.");
  el.connectedCallback();

  await el._toggle();

  assert.equal(el.dataset.state, "degraded");
  assert.equal(harness.speech.calls.speak.length, 1);
  assert.equal(harness.speech.calls.speak[0].text, "Hello there.");
});

test("malformed timings JSON on an otherwise-ok response does not crash playback", async () => {
  const harness = load({
    fetch: async (url) => ({
      ok: true,
      status: 200,
      url,
      json: async () => { throw new SyntaxError("bad json"); },
    }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle();

  assert.equal(harness.audioInstances.length, 1, "audio still plays even if timings parsing fails");
});
