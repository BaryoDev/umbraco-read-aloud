"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { load } = require("./harness.js");

const WORDS = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

function boundariesFor(words) {
  return words.map((w, i) => ({ text: w, offsetMs: i * 100, durationMs: 90 }));
}

// readaloud.js runs inside a vm.createContext sandbox, so an array it builds (Highlighter.
// alignBoundaries returns one) has that sandbox's own Array prototype, not this file's. Node's
// strict deepEqual treats that as "same structure but not reference-equal" even when every value
// matches, so plain() re-hosts the array as an ordinary array in this realm before comparing.
function plain(arr) { return Array.from(arr); }

test("twelve spans and twelve matching boundaries align, and highlighting activates on the real span", async () => {
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => boundariesFor(WORDS) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#article");
  harness.registry["#article"] = harness.createTarget(WORDS.join(" "));
  el.connectedCallback();

  await el._toggle();

  assert.equal(el._highlighter.wordCount, 12, "the real word-wrap must have produced twelve spans");
  assert.deepEqual(plain(el._boundaryMap), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

  el._audioEl.currentTime = 0.35; // 350ms, inside word index 3's window
  el._onTimeUpdate();
  assert.equal(el._highlighter.active, 3, "the fourth span must be the one actually highlighted");
});

test("boundaries with no matching span stop highlighting from the point of mismatch, without disturbing already-aligned words", async () => {
  const boundaryWords = [...WORDS, "thirteen", "fourteen", "fifteen"]; // 15 total, last 3 have no span
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => boundariesFor(boundaryWords) }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#article");
  harness.registry["#article"] = harness.createTarget(WORDS.join(" ")); // still just twelve spans
  el.connectedCallback();

  await el._toggle();

  assert.equal(el._highlighter.wordCount, 12);
  assert.equal(el._boundaryMap.length, 15);
  assert.deepEqual(
    plain(el._boundaryMap).slice(0, 12),
    [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
    "the twelve real words must still align even though three trailing boundaries do not exist as spans",
  );
  assert.equal(el._boundaryMap[12], -1, "a boundary with nothing to match against is unmapped, not guessed");
  assert.equal(el._boundaryMap[13], -1);
  assert.equal(el._boundaryMap[14], -1);

  el._audioEl.currentTime = 1.15; // 1150ms, the last real word's window
  el._onTimeUpdate();
  assert.equal(el._highlighter.active, 11);

  el._audioEl.currentTime = 1.25; // 1250ms, into the unmapped phantom tail
  el._onTimeUpdate();
  assert.equal(
    el._highlighter.active,
    11,
    "must not advance onto a phantom word once the thread is genuinely lost",
  );
});

test("punctuation and capitalisation differences between spoken boundaries and rendered text do not disable highlighting", async () => {
  const boundaries = [
    { text: "Hello", offsetMs: 0, durationMs: 90 },
    { text: "world", offsetMs: 100, durationMs: 90 },
    { text: "friend", offsetMs: 200, durationMs: 90 },
  ];
  const harness = load({
    fetch: async (url) => ({ ok: true, status: 200, url, json: async () => boundaries }),
  });

  const el = new harness.ElementClass();
  el.setAttribute("node", "abc");
  el.setAttribute("for", "#article");
  // The DOM carries punctuation and capitalisation the plain spoken word text does not.
  harness.registry["#article"] = harness.createTarget("Hello, world friend.");
  el.connectedCallback();

  await el._toggle();

  assert.deepEqual(
    plain(el._boundaryMap),
    [0, 1, 2],
    "ordinary punctuation differences must not disable the feature wholesale",
  );
});
