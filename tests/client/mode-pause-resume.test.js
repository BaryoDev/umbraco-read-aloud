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

test("pausing and resuming the speech synthesis fallback uses pause/resume, not a restart", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  withSpeechTarget(harness, el);
  el.connectedCallback();

  await el._toggle(); // press: 503 -> degrade -> speak
  assert.equal(harness.speech.calls.speak.length, 1);
  harness.speech.calls.speak[0].onstart();
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
    harness.speech.calls.speak[0].onstart();

    await el._toggle(); // pause: must reach speech, not the now-dead audio element

    assert.equal(harness.speech.calls.pause, 1);
    assert.equal(
      harness.audioInstances[0].pauseCalls,
      0,
      "the dead audio element must not receive pause() once the mode has switched to speech",
    );
  },
);

test("a repeated degrade does not restart the utterance from the beginning", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  withSpeechTarget(harness, el);
  el.connectedCallback();

  await el._toggle();
  assert.equal(harness.speech.calls.speak.length, 1);
  harness.speech.calls.speak[0].onstart();

  el._degrade(); // a second, redundant failure signal while already speaking

  assert.equal(harness.speech.calls.speak.length, 1, "must not call speak() a second time");
});
