"use strict";

/**
 * Loads readaloud.js into a small, hand-built browser stub and hands back the captured
 * `ReadAloudElement` class plus everything a test needs to drive and inspect it.
 *
 * Deliberately not a real DOM. The client's own word-wrapping (`Highlighter.prepare`, which walks
 * a real `TreeWalker`) is left unexercised here: reproducing `TreeWalker`/`NodeFilter` traversal
 * faithfully is a project in itself, and every behaviour this harness exists to catch (status
 * routing, request count, pause/resume across audio and speech, abort on disconnect, cross-element
 * isolation, path base, per-press `for` resolution) is observable without it. `document.
 * createTreeWalker` is stubbed to return no nodes, so any code path that reaches it degrades to
 * "no words found" instead of throwing.
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

class StubElement {
  constructor(tag) {
    this.tagName = tag ? String(tag).toUpperCase() : "DIV";
    this._attrs = {};
    this.dataset = {};
    this.children = [];
    this.parentNode = null;
    this.disabled = false;
    this._innerHTML = "";
    this._textContent = "";
    this._listeners = {};
    this.isConnected = true;
    const self = this;
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

  appendChild(child) {
    this.children.push(child);
    child.parentNode = this;
    return child;
  }

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

  const documentStub = {
    currentScript: options.currentScript || null,
    baseURI: "http://localhost/",
    head: { appendChild() {} },
    getElementById: () => null,
    createElement: (tag) => new StubElement(tag),
    createDocumentFragment: () => new StubElement("#fragment"),
    createTextNode: (text) => ({ nodeType: 3, nodeValue: text }),
    // No real traversal: see the file-level comment above.
    createTreeWalker: () => ({ nextNode: () => false, currentNode: null }),
    querySelector: (sel) => (sel ? registry[sel] || null : null),
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
    /** Creates a plain target element (for #selector registration) with the given text. */
    createTarget(text) {
      const el = new StubElement("div");
      el.textContent = text;
      return el;
    },
  };
}

module.exports = { load, StubElement };
