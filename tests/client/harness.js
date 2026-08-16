"use strict";

/**
 * Loads readaloud.js into a small, hand-built browser stub and hands back the captured
 * `ReadAloudElement` class plus everything a test needs to drive and inspect it.
 *
 * Deliberately not a real DOM, but `document.createTreeWalker` does a real (simplified) recursive
 * walk over a target's `children`, so `Highlighter.prepare()` genuinely runs: it produces real
 * spans from real text, which is what the alignment tests in highlight-alignment.test.js exercise.
 * What is simplified: no attribute selectors beyond a literal tag-name check in `matches()`, no
 * mixed inline/block nesting depth limits, no `NodeIterator`/`Range` semantics. That is enough for
 * this package's own skip-selector list (`code`, `pre`, `script`, `style`) and for every test in
 * this suite; it is not a general-purpose `TreeWalker`.
 */

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SOURCE_PATH = path.join(
  __dirname,
  "..",
  "..",
  "src",
  "BaryoDev.Umbraco.ReadAloud",
  "wwwroot",
  "readaloud.js",
);

const TEXT_NODE = 3;

class StubElement {
  constructor(tag) {
    this.tagName = tag ? String(tag).toUpperCase() : "DIV";
    this.nodeType = 1; // ELEMENT_NODE
    this._attrs = {};
    this.dataset = {};
    this.children = [];
    this.parentNode = null;
    this.parentElement = null;
    this.disabled = false;
    this._innerHTML = "";
    this._textContent = "";
    this._listeners = {};
    this.isConnected = true;
    this.classList = {
      _set: new Set(),
      add(c) { this._set.add(c); },
      remove(c) { this._set.delete(c); },
      contains(c) { return this._set.has(c); },
    };
  }

  get innerHTML() { return this._innerHTML; }
  set innerHTML(v) { this._innerHTML = v; }

  get textContent() { return this._textContent; }
  set textContent(v) { this._textContent = v; }

  setAttribute(name, value) { this._attrs[name] = String(value); }
  getAttribute(name) {
    return Object.prototype.hasOwnProperty.call(this._attrs, name) ? this._attrs[name] : null;
  }
  hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this._attrs, name); }
  removeAttribute(name) { delete this._attrs[name]; }

  /** Only a literal tag-name check (e.g. "code,pre"): enough for this package's own skip list. */
  matches(selector) {
    if (!selector) return false;
    const tags = selector.split(",").map((s) => s.trim().toLowerCase());
    return tags.includes(this.tagName.toLowerCase());
  }

  appendChild(child) {
    this.children.push(child);
    child.parentNode = this;
    child.parentElement = this;
    return child;
  }

  /** Real DocumentFragment semantics: the fragment's own children move into the parent. */
  replaceChild(newChild, oldChild) {
    const idx = this.children.indexOf(oldChild);
    if (idx === -1) return oldChild;
    const insert = newChild.tagName === "#FRAGMENT" ? newChild.children.slice() : [newChild];
    this.children.splice(idx, 1, ...insert);
    for (const node of insert) {
      node.parentNode = this;
      node.parentElement = this;
    }
    oldChild.parentNode = null;
    oldChild.parentElement = null;
    return oldChild;
  }

  /** No adjacent-text-node merging: nothing in this suite depends on it. */
  normalize() {}

  scrollIntoView() {}

  querySelector() { return null; }

  addEventListener(type, cb) {
    (this._listeners[type] = this._listeners[type] || []).push(cb);
  }
  removeEventListener(type, cb) {
    const list = this._listeners[type];
    if (!list) return;
    const i = list.indexOf(cb);
    if (i >= 0) list.splice(i, 1);
  }
  dispatchEvent(evt) {
    const list = this._listeners[evt.type] || [];
    for (const cb of list.slice()) cb(evt);
    return true;
  }

  remove() {
    this._removed = true;
    this.isConnected = false;
    if (this.parentNode) {
      const idx = this.parentNode.children.indexOf(this);
      if (idx >= 0) this.parentNode.children.splice(idx, 1);
      this.parentNode = null;
    }
  }
}

/** A stand-in for the real `<audio>` element, recorded on the harness for assertions. */
function makeAudioClass(instances) {
  return class StubAudio extends StubElement {
    constructor() {
      super("audio");
      this.currentTime = 0;
      this.duration = 100;
      this.preload = "";
      this.playCalls = 0;
      this.pauseCalls = 0;
      this._src = "";
      instances.push(this);
    }
    get src() { return this._src; }
    set src(v) { this._src = v; }
    play() {
      this.playCalls++;
      return Promise.resolve();
    }
    pause() {
      this.pauseCalls++;
    }
  };
}

class StubUtterance {
  constructor(text) {
    this.text = text;
    this.onstart = null;
    this.onend = null;
    this.onerror = null;
  }
}

/** A real, if simplified, TreeWalker: SHOW_TEXT only, recursing through `.children`. */
function createTreeWalker(root, whatToShow, filter) {
  const acceptNode = filter && filter.acceptNode;
  const queue = [];
  (function collect(node) {
    if (!node) return;
    if (node.nodeType === TEXT_NODE) {
      queue.push(node);
      return;
    }
    for (const child of node.children || []) collect(child);
  })(root);

  let i = -1;
  return {
    get currentNode() { return queue[i]; },
    nextNode() {
      while (++i < queue.length) {
        const node = queue[i];
        const result = acceptNode ? acceptNode(node) : 1;
        if (result === 1 /* FILTER_ACCEPT */) return true;
      }
      return false;
    },
  };
}

/**
 * Builds a fresh sandbox, evaluates readaloud.js in it, and returns:
 *   - ElementClass: the class handed to customElements.define("read-aloud", ...)
 *   - registry: a selector -> element map backing document.querySelector, mutable by the test
 *   - fetchCalls: [{ url, init }], in call order
 *   - audioInstances: every `new Audio()` the client created, in creation order
 *   - speech: the speechSynthesis stub, with .calls = { speak: [utterance...], cancel, pause, resume }
 *
 * options:
 *   fetch(url, init) -> Promise<ResponseLike>   required for tests that press play
 *   currentScript: { src } | null               simulates document.currentScript for base detection
 *   scripts: [{ src, type }]                    the page's <script src> elements, which is all a
 *                                               module script has: currentScript is null for one
 */
function load(options) {
  options = options || {};

  const source = fs.readFileSync(SOURCE_PATH, "utf8");

  const registry = {};
  const fetchCalls = [];
  const audioInstances = [];

  const speech = {
    calls: { speak: [], cancel: 0, pause: 0, resume: 0 },
    speaking: false,
    speak(utterance) {
      this.calls.speak.push(utterance);
      this.speaking = true;
    },
    cancel() {
      this.calls.cancel++;
      this.speaking = false;
    },
    pause() { this.calls.pause++; },
    resume() { this.calls.resume++; },
  };

  let capturedClass = null;
  const customElements = {
    get(name) { return name === "read-aloud" ? capturedClass : undefined; },
    define(name, cls) { capturedClass = cls; },
  };

  const fetchImpl = options.fetch || (() => Promise.reject(new Error("no fetch stub configured")));
  function fetchStub(url, init) {
    fetchCalls.push({ url, init });
    return fetchImpl(url, init);
  }

  // The <script> elements the page carries. A module script is not `document.currentScript` while
  // it runs, but it is still in the document, so this is what base detection has to read under the
  // tag the README documents.
  const scripts = (options.scripts || []).map((script) => {
    const el = new StubElement("script");
    el.src = script.src;
    if (script.type) el.setAttribute("type", script.type);
    return el;
  });

  const documentStub = {
    currentScript: options.currentScript || null,
    baseURI: "http://localhost/",
    head: { appendChild() {} },
    getElementById: () => null,
    createElement: (tag) => new StubElement(tag),
    createDocumentFragment: () => new StubElement("#fragment"),
    createTextNode: (text) => ({ nodeType: TEXT_NODE, nodeValue: text, parentNode: null, parentElement: null }),
    createTreeWalker,
    querySelector: (sel) => (sel ? registry[sel] || null : null),
    /** Only the one selector this client uses; anything else is an empty list rather than a lie. */
    querySelectorAll: (sel) => (sel === "script[src]" ? scripts.slice() : []),
  };

  const sandbox = {
    console,
    HTMLElement: StubElement,
    Audio: makeAudioClass(audioInstances),
    SpeechSynthesisUtterance: StubUtterance,
    NodeFilter: { SHOW_TEXT: 4, FILTER_ACCEPT: 1, FILTER_REJECT: 2 },
    document: documentStub,
    fetch: fetchStub,
    customElements,
    matchMedia: () => ({ matches: false }),
    AbortController: globalThis.AbortController,
    URL: globalThis.URL,
    encodeURIComponent,
    speechSynthesis: speech,
  };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;

  vm.createContext(sandbox);
  vm.runInContext(source, sandbox, { filename: "readaloud.js" });

  return {
    ElementClass: capturedClass,
    registry,
    fetchCalls,
    audioInstances,
    speech,
    document: documentStub,
    /**
     * Creates a plain target element with the given text, wired both ways: `.textContent` directly
     * (what `_speak()`'s fallback-text path reads) and as one real child text node (what
     * `Highlighter.prepare()`'s TreeWalker walk discovers). A real `<div>text</div>` only has the
     * second; both are set here because the two code paths under test read different ones.
     */
    createTarget(text) {
      const el = new StubElement("div");
      el.textContent = text;
      const node = documentStub.createTextNode(text);
      el.appendChild(node);
      return el;
    },
  };
}

module.exports = { load, StubElement };
