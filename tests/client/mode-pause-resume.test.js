"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

function withSpeechTarget(harness, el, text) {
  el.setAttribute("for", "#article");
  harness.registry["#article"] = harness.createTarget(text || "Hello there.");
}

test("pausing and resuming audio playback pauses and replays the audio element", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => [] }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle(); // press: starts audio
  harness.audioInstances[0].dispatchEvent({ type: "playing" });
  assert.equal(el._state, "playing");

  await el._toggle(); // pause
  // A real <audio> element fires its own "pause" event when .pause() is called; simulate it.
  harness.audioInstances[0].dispatchEvent({ type: "pause" });
  assert.equal(harness.audioInstances[0].pauseCalls, 1);
  assert.equal(harness.speech.calls.pause, 0);
  assert.equal(el._state, "paused");

  await el._toggle(); // resume
  assert.equal(harness.audioInstances[0].playCalls, 2);
  assert.equal(harness.speech.calls.resume, 0);
});

test("speak() renders playing immediately, without waiting for the browser's onstart event", async () => {
  // A real browser fires onstart asynchronously, sometimes considerably later, and Chrome is
  // known to silently no-op a speak() call before its voices have loaded, firing neither onstart
  // nor onerror at all. Waiting for onstart to unstick the button leaves it disabled at
  // "Loading..." forever in that case, the exact outcome the fallback exists to prevent.
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  withSpeechTarget(harness, el);
  el.connectedCallback();

  await el._toggle(); // 503 -> degrade -> speak, and nothing ever calls onstart

  assert.equal(harness.speech.calls.speak.length, 1);
  assert.equal(el._state, "playing", "the button must not be stuck at loading pending onstart");
  assert.equal(el._button.disabled, false);
});

test("pausing and resuming the speech synthesis fallback uses pause/resume, not a restart", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  withSpeechTarget(harness, el);
  el.connectedCallback();

  await el._toggle(); // press: 503 -> degrade -> speak, rendered playing immediately
  assert.equal(harness.speech.calls.speak.length, 1);
  assert.equal(el._state, "playing");

  await el._toggle(); // pause
  assert.equal(harness.speech.calls.pause, 1, "speechSynthesis.pause() must be called");
  assert.equal(el._state, "paused");

  await el._toggle(); // resume
  assert.equal(harness.speech.calls.resume, 1, "speechSynthesis.resume() must be called");
  assert.equal(harness.speech.calls.speak.length, 1, "resuming must not start a new utterance");
});

test(
  "a media error after a successful probe degrades to speech and pause/resume then controls speech, not the dead audio element",
  async () => {
    const harness = load({
      fetch: async (url) => ({ ok: true, status: 200, url, json: async () => [] }),
    });

    const el = new harness.ElementClass();
    el.setAttribute("node", "abc");
    withSpeechTarget(harness, el);
    el.connectedCallback();

    await el._toggle(); // press: probe ok, audio element created
    assert.equal(harness.audioInstances.length, 1);

    harness.audioInstances[0].dispatchEvent({ type: "error" }); // the real request 429s/fails
    assert.equal(el.dataset.state, "degraded");
    assert.equal(harness.speech.calls.speak.length, 1);
    assert.equal(el._state, "playing", "rendered immediately, no onstart needed");

    await el._toggle(); // pause: must reach speech, not the now-dead audio element

    assert.equal(harness.speech.calls.pause, 1);
    assert.equal(
      harness.audioInstances[0].pauseCalls,
      0,
      "the dead audio element must not receive pause() once the mode has switched to speech",
    );
  },
);

test("a repeated degrade before onstart fires does not start a second overlapping utterance", async () => {
  // The real race this guards: a play() rejection followed by the media error event, both
  // landing synchronously while nothing has called onstart yet. A guard keyed on a render state
  // that only flips to "playing" inside onstart cannot see this window at all.
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  withSpeechTarget(harness, el);
  el.connectedCallback();

  await el._toggle();
  assert.equal(harness.speech.calls.speak.length, 1);

  el._degrade(); // a second, redundant failure signal, before onstart has ever fired

  assert.equal(harness.speech.calls.speak.length, 1, "must not call speak() a second time");
});

test("a stray data-state=\"degraded\" attribute does not silently disable the server route forever", async () => {
  // dataset.state is a public attribute; an editor (or a Razor template, or another script)
  // writing <read-aloud data-state="degraded"> must not be able to disable the real endpoint by
  // accident. The decision has to live on something private.
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => [] }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();
  el.dataset.state = "degraded"; // simulates markup or external code setting this directly

  await el._toggle();

  assert.equal(harness.fetchCalls.length, 1, "the server route must still be probed");
  assert.equal(harness.audioInstances.length, 1);
  assert.equal(harness.speech.calls.speak.length, 0);
});
