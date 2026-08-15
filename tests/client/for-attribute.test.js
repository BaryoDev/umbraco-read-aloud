"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

test("the for target is resolved at press time, not cached from upgrade time", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: false, status: 503, url, json: async () => ({}) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#late");
  // Nothing registered for "#late" yet: a real page where the target renders after this element.
  el.connectedCallback();

  // Now the target exists in the DOM.
  harness.registry["#late"] = harness.createTarget("hello world");

  await el._toggle();

  assert.equal(harness.speech.calls.speak.length, 1);
  assert.equal(
    harness.speech.calls.speak[0].text,
    "hello world",
    "the target must be looked up again at press time",
  );
});
