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

test("a reconnected element (a DOM move, a tab switch, an accordion) is usable again, not stuck loading forever", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => [] }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();
  el.disconnectedCallback(); // e.g. the framework detaches and reattaches the element
  el.connectedCallback(); // the idempotency guard must not leave `_active` stuck false

  await el._toggle();

  assert.equal(
    harness.audioInstances.length,
    1,
    "a press after reconnecting must reach audio setup rather than bailing at the _active check",
  );
});

test("a media error that fires after disconnect does not start reading into a removed element", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => [] }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#article");
  harness.registry["#article"] = harness.createTarget("Hello there.");
  el.connectedCallback();

  await el._toggle(); // audio mode set up, one request already in flight for the media itself
  const audio = harness.audioInstances[0];

  el.disconnectedCallback(); // the reader navigated away; the button and element are gone

  // The in-flight media request now fails, exactly finding 5's cold-synthesis scenario reached
  // through the audio element's own error event rather than the timings probe.
  audio.dispatchEvent({ type: "error" });

  assert.equal(
    harness.speech.calls.speak.length,
    0,
    "a media error after disconnect must not start speaking into a page with no button",
  );
});
