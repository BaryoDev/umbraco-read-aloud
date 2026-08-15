"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

test("aria-live sits on a separate status element, not the button that also carries a changing aria-label", () => {
  const harness = load({ fetch: async () => ({ ok: true, status: 200, url: "", json: async () => [] }) });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.connectedCallback();

  assert.equal(
    el._button.getAttribute("aria-live"),
    null,
    "the button's own aria-label changes on every state; aria-live there re-announces the whole control",
  );

  const live = el.children.find((c) => c.getAttribute && c.getAttribute("aria-live") === "polite");
  assert.ok(live, "a separate live region must exist for state announcements");
});
