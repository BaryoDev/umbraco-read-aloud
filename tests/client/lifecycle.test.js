"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

test("disconnecting before a cold probe resolves aborts it and never starts audio", async () => {
  let resolveFetch;
  const fetchPromise = new Promise((resolve) => { resolveFetch = resolve; });

  const harness = load({
    fetch: async (url) => {
      await fetchPromise;
      return { ok: true, status: 200, url, json: async () => [] };
    },
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  const toggling = el._toggle();
  el.disconnectedCallback();

  const signal = harness.fetchCalls[0].init && harness.fetchCalls[0].init.signal;
  assert.ok(signal, "the fetch must be issued with an abort signal");
  assert.equal(signal.aborted, true, "disconnecting must abort the in-flight probe");

  resolveFetch();
  await toggling;

  assert.equal(harness.audioInstances.length, 0, "no audio element is built for a resumed continuation");
});

test("one element's disconnect does not silence another element's speech fallback", async () => {
  const harness = load({
    fetch: async (url) => {
      if (url.includes("node-a")) return { ok: false, status: 404, url, json: async () => ({}) };
      return { ok: false, status: 503, url, json: async () => ({}) };
    },
  });

  const elA = new harness.ElementClass();
  elA.setAttribute("node", "node-a");
  elA.connectedCallback();
  await elA._toggle(); // 404s and removes itself, never touches speech

  const elB = new harness.ElementClass();
  elB.setAttribute("node", "node-b");
  elB.setAttribute("for", "#article-b");
  harness.registry["#article-b"] = harness.createTarget("Hello from B.");
  elB.connectedCallback();
  await elB._toggle(); // 503s and degrades to speech
  assert.equal(harness.speech.calls.speak.length, 1);

  elA.disconnectedCallback();

  assert.equal(harness.speech.calls.cancel, 0, "an element that never spoke must not cancel the shared queue");
});

test("a second connectedCallback call does not duplicate the button", () => {
  const harness = load({ fetch: async () => ({ ok: true, status: 200, url: "", json: async () => [] }) });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();
  const countAfterFirst = el.children.length;

  el.connectedCallback();

  assert.equal(el.children.length, countAfterFirst, "a repeated connect must not append a second button");
});
