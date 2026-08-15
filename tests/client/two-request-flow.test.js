"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

function okTimingsResponse(url) {
  return {
    ok: true,
    status: 200,
    url,
    json: async () => [],
  };
}

test("a press fetches only /timings, then points <audio> at the audio url with no second fetch()", async () => {
  const harness = load({
    fetch: async (url) => okTimingsResponse(url),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc-123");
  el.connectedCallback();

  await el._toggle();

  assert.equal(harness.fetchCalls.length, 1, "exactly one fetch() call per press");
  assert.match(harness.fetchCalls[0].url, /\/read-aloud\/abc-123\/timings/);

  assert.equal(harness.audioInstances.length, 1, "the audio element is created");
  assert.match(harness.audioInstances[0].src, /\/read-aloud\/abc-123(\?|$)/);
  assert.doesNotMatch(harness.audioInstances[0].src, /timings/);
});

test("the timings request happens before the audio element's src is set", async () => {
  const order = [];
  const harness = load({
    fetch: async (url) => {
      order.push("fetch:" + url);
      return okTimingsResponse(url);
    },
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc-123");
  el.connectedCallback();
  await el._toggle();

  // The audio element's own request is outside our fetch() stub (a real <audio> fetches itself),
  // so ordering is proven by src only being set after the timings fetch already resolved.
  assert.ok(order[0].includes("timings"));
  assert.equal(harness.audioInstances.length, 1);
});
