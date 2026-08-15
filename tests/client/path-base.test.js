"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

function okTimings(url) {
  return { ok: true, status: 200, url, json: async () => [] };
}

test("an explicit base attribute prefixes both routes", async () => {
  const harness = load({ fetch: async (url) => okTimings(url) });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("base", "/myapp");
  el.connectedCallback();

  await el._toggle();

  assert.match(harness.fetchCalls[0].url, /^\/myapp\/read-aloud\/abc\/timings/);
  assert.match(harness.audioInstances[0].src, /^\/myapp\/read-aloud\/abc/);
});

test("with no base attribute, the prefix is derived from the served script's own url", async () => {
  const harness = load({
    fetch: async (url) => okTimings(url),
    currentScript: { src: "http://localhost/myapp/App_Plugins/BaryoDev.ReadAloud/readaloud.js" },
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle();

  assert.match(harness.fetchCalls[0].url, /^\/myapp\/read-aloud\/abc\/timings/);
});

test("at the site root, with no base attribute and no currentScript, routes stay root-relative", async () => {
  const harness = load({ fetch: async (url) => okTimings(url) });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle();

  assert.match(harness.fetchCalls[0].url, /^\/read-aloud\/abc\/timings/);
});
