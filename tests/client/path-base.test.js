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

test("under the documented type=module tag, the prefix still comes from the script element", async () => {
  // The deployment the README prescribes. `document.currentScript` is null for the whole time a
  // module script is executing, so detection that reads only that is inert on every site that
  // follows the documentation. On a site under a path prefix that means requesting
  // /read-aloud/... at the host root, taking the 404, and removing the button.
  const harness = load({
    fetch: async (url) => okTimings(url),
    currentScript: null,
    scripts: [
      {
        src: "http://localhost/myapp/App_Plugins/BaryoDev.ReadAloud/readaloud.js",
        type: "module",
      },
    ],
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  await el._toggle();

  assert.match(harness.fetchCalls[0].url, /^\/myapp\/read-aloud\/abc\/timings/);
  assert.match(harness.audioInstances[0].src, /^\/myapp\/read-aloud\/abc/);
  assert.equal(el._removed, undefined, "the button must not remove itself");
});

test("another site's script tags are not mistaken for this one", async () => {
  // A real page has several. Only the one served from this package's own path says anything about
  // where the app is mounted, and taking a prefix off any other would be worse than taking none.
  const harness = load({
    fetch: async (url) => okTimings(url),
    currentScript: null,
    scripts: [
      { src: "https://cdn.example.com/analytics/v3/tracker.js" },
      { src: "http://localhost/assets/site.js" },
      { src: "http://localhost/myapp/App_Plugins/BaryoDev.ReadAloud/readaloud.js", type: "module" },
    ],
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
