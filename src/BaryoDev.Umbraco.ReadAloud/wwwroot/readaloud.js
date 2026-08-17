/**
 * BaryoDev.Umbraco.ReadAloud browser client.
 *
 * A single, hand-written, dependency-free ES module. No build step: drop it in with
 * `<script type="module" src="/App_Plugins/BaryoDev.ReadAloud/readaloud.js"></script>` and use
 * `<read-aloud node="..." for="#selector" voice="..."></read-aloud>` in markup.
 *
 * Server contract (both anonymous, both accept ?voice=):
 *   GET /read-aloud/{nodeKey}           audio/mpeg
 *   GET /read-aloud/{nodeKey}/timings   application/json, an array of { text, offsetMs, durationMs }
 *
 * A press fetches /timings first, as the status probe, and reads it directly rather than blocking
 * on it before a second, separate request. Both routes synthesize on a cold article and block for
 * the full duration; the server does not stream, so requesting audio first buys nothing, and
 * leading with the small JSON response keeps this down to two requests per press: the /timings
 * fetch this code makes, and the one request the browser's own <audio> element makes once its src
 * is set. A media error can still occur on that second request and is treated as a degradation,
 * the same as a 503 from the probe.
 */
(function () {
  "use strict";

  const DEFAULT_SKIP = ["code", "pre", "script", "style", "[data-read-aloud-skip]"];
  const HIGHLIGHT_STYLE_ID = "read-aloud-highlight-style";
  const BUTTON_STYLE_ID = "read-aloud-btn-style";
  const SCRIPT_SUFFIX = "/App_Plugins/BaryoDev.ReadAloud/readaloud.js";

  /** Lowercased, with leading/trailing punctuation stripped. Internal hyphens/apostrophes survive
   * (hyphenation, possessives), which is the point: strip what differs between "word." and "word",
   * not what makes two different words look alike. */
  function normalizeWord(s) {
    return (s || "").toLowerCase().replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, "");
  }

  const ICONS = {
    play: '<svg viewBox="0 0 24 24" width="1.1em" height="1.1em" fill="currentColor" aria-hidden="true"><path d="M8 5v14l11-7z"/></svg>',
    pause: '<svg viewBox="0 0 24 24" width="1.1em" height="1.1em" fill="currentColor" aria-hidden="true"><path d="M6 5h4v14H6zM14 5h4v14h-4z"/></svg>',
    loading: '<svg viewBox="0 0 24 24" width="1.1em" height="1.1em" fill="none" stroke="currentColor" stroke-width="2.5" aria-hidden="true" class="read-aloud-spin"><path stroke-linecap="round" d="M12 3a9 9 0 1 0 9 9"/></svg>',
  };

  /**
   * Wraps the words of an element in spans (in place, keeping bold/links/etc.) and highlights one
   * at a time. Every word comes from `textContent` on real DOM nodes already in the page, never
   * from server text, so this never touches innerHTML with anything the server sent.
   */
  class Highlighter {
    constructor(root, opts) {
      opts = opts || {};
      this.root = root;
      this.spans = [];
      this.active = -1;
      this.prepared = false;
      this.opts = {
        activeClass: opts.activeClass || "read-aloud-word--active",
        wordClass: opts.wordClass || "read-aloud-word",
        scroll: opts.scroll !== false,
        scrollBlock: opts.scrollBlock || "center",
        skipSelectors: opts.skipSelectors || DEFAULT_SKIP,
        injectStyle: opts.injectStyle !== false,
      };
    }

    get wordCount() { return this.spans.length; }

    /**
     * Maps each boundary to the span it corresponds to, walking both sequences forward together.
     * Boundaries come from the server's spoken text; spans come from whitespace-splitting whatever
     * is in the DOM right now, and the two tokenisers disagree on punctuation, quotes, hyphenation
     * and possessives long before any structural difference (a skipped `<code>` block, an edited
     * property) ever comes into it. A small lookahead resyncs past those ordinary differences so a
     * comma does not disable highlighting for an entire article; only when no match turns up within
     * that lookahead is the thread treated as genuinely lost, and only the remainder goes unmapped
     * (-1) rather than the whole map, so everything already aligned stays aligned.
     */
    alignBoundaries(boundaries) {
      if (!this.prepared) this.prepare();
      const spans = this.spans;
      const map = new Array(boundaries.length).fill(-1);
      const maxLookahead = 4;
      let si = 0;
      for (let bi = 0; bi < boundaries.length; bi++) {
        const target = normalizeWord(boundaries[bi] && boundaries[bi].text);
        if (!target) continue; // nothing to match (a boundary that is pure punctuation/silence)
        let found = -1;
        for (let look = 0; look <= maxLookahead && si + look < spans.length; look++) {
          if (normalizeWord(spans[si + look].textContent) === target) {
            found = si + look;
            break;
          }
        }
        if (found === -1) break; // the thread is genuinely lost; leave this and the rest unmapped
        map[bi] = found;
        si = found + 1;
      }
      return map;
    }

    prepare() {
      if (this.prepared || typeof document === "undefined") return;
      if (this.opts.injectStyle) this._injectStyle();

      const skip = this.opts.skipSelectors.join(",");
      const root = this.root;
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: function (node) {
          if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
          let el = node.parentElement;
          while (el && el !== root.parentElement) {
            if (skip && el.matches(skip)) return NodeFilter.FILTER_REJECT;
            el = el.parentElement;
          }
          return NodeFilter.FILTER_ACCEPT;
        },
      });

      const textNodes = [];
      while (walker.nextNode()) textNodes.push(walker.currentNode);

      for (const node of textNodes) {
        const parts = (node.nodeValue || "").split(/(\s+)/);
        const frag = document.createDocumentFragment();
        for (const part of parts) {
          if (!part) continue;
          if (/^\s+$/.test(part)) {
            frag.appendChild(document.createTextNode(part));
          } else {
            const span = document.createElement("span");
            span.className = this.opts.wordClass;
            span.textContent = part;
            frag.appendChild(span);
            this.spans.push(span);
          }
        }
        node.parentNode && node.parentNode.replaceChild(frag, node);
      }
      this.prepared = true;
    }

    highlight(index) {
      if (!this.prepared) this.prepare();
      if (index === this.active) return;
      const prev = this.spans[this.active];
      if (prev) prev.classList.remove(this.opts.activeClass);
      this.active = index;
      const span = this.spans[index];
      if (!span) return;
      span.classList.add(this.opts.activeClass);
      if (this.opts.scroll) {
        const reduce = typeof matchMedia !== "undefined" && matchMedia("(prefers-reduced-motion: reduce)").matches;
        span.scrollIntoView({ block: this.opts.scrollBlock, inline: "nearest", behavior: reduce ? "auto" : "smooth" });
      }
    }

    clear() {
      const span = this.spans[this.active];
      if (span) span.classList.remove(this.opts.activeClass);
      this.active = -1;
    }

    destroy() {
      this.clear();
      for (const span of this.spans) {
        const text = document.createTextNode(span.textContent || "");
        span.parentNode && span.parentNode.replaceChild(text, span);
      }
      this.spans = [];
      this.prepared = false;
      this.root.normalize();
    }

    _injectStyle() {
      if (document.getElementById(HIGHLIGHT_STYLE_ID)) return;
      const style = document.createElement("style");
      style.id = HIGHLIGHT_STYLE_ID;
      style.textContent =
        "." + this.opts.activeClass + "{background:rgba(250,204,21,.45);border-radius:.15em;" +
        "box-shadow:0 0 0 .1em rgba(250,204,21,.45);transition:background .1s ease}";
      document.head.appendChild(style);
    }
  }

  function injectButtonStyle() {
    if (typeof document === "undefined" || document.getElementById(BUTTON_STYLE_ID)) return;
    const style = document.createElement("style");
    style.id = BUTTON_STYLE_ID;
    style.textContent =
      ".read-aloud-btn{display:inline-flex;align-items:center;gap:.5em;padding:.5em .9em;font:inherit;" +
      "font-weight:600;line-height:1;color:#fff;background:#2563eb;border:none;border-radius:999px;" +
      "cursor:pointer;transition:background .15s ease,opacity .15s ease}" +
      ".read-aloud-btn:hover{background:#1d4ed8}" +
      ".read-aloud-btn:disabled{opacity:.7;cursor:default}" +
      '.read-aloud-btn[data-state="playing"]{background:#dc2626}' +
      '.read-aloud-btn[data-state="playing"]:hover{background:#b91c1c}' +
      ".read-aloud-btn__icon{display:inline-flex}" +
      ".read-aloud-spin{animation:read-aloud-spin .8s linear infinite;transform-origin:center}" +
      "@keyframes read-aloud-spin{to{transform:rotate(360deg)}}" +
      "@media (prefers-reduced-motion: reduce){.read-aloud-spin{animation:none}}" +
      // Visually hidden but still announced: aria-live lives here, not on the button, so a state
      // change is spoken once rather than re-announcing the whole button on every press.
      ".read-aloud-sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;" +
      "overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}";
    document.head.appendChild(style);
  }

  function resolveTarget(selector) {
    if (!selector) return null;
    return document.querySelector(selector);
  }

  /** The prefix of a URL that ends in this script's own served path, or "" if it does not. */
  function prefixOf(src) {
    if (!src) return "";
    try {
      const p = new URL(src, document.baseURI || undefined).pathname;
      if (p.length > SCRIPT_SUFFIX.length && p.slice(p.length - SCRIPT_SUFFIX.length) === SCRIPT_SUFFIX) {
        return p.slice(0, p.length - SCRIPT_SUFFIX.length);
      }
    } catch (err) {
      // Not a URL this code can read. No prefix.
    }
    return "";
  }

  /**
   * Derives the site's path base (an IIS virtual application, `UsePathBase`, anything that puts
   * the app under a prefix) from the URL this very script was served from.
   *
   * `document.currentScript` is null for the whole time a `type="module"` script is executing, and
   * `type="module"` is exactly what the README prescribes, so reading only that leaves detection
   * inert on every site that follows the documentation. The script element is still in the
   * document either way, so the page's own `<script src>` tags are searched for the one this file
   * was served from. Both are read: `currentScript` is exact when it is there (a classic tag, or
   * one injected and then removed), and the search covers the module case and anything else.
   *
   * Only a tag whose path ends in this package's own served path counts. Every other script on the
   * page belongs to somebody else and says nothing about where this app is mounted.
   *
   * The `base` attribute on the element still takes priority whenever it is set, and is still the
   * answer for a site that serves this file from somewhere other than App_Plugins.
   */
  function detectBase() {
    if (typeof document === "undefined") return "";

    const current = document.currentScript;
    const fromCurrent = prefixOf(current && current.src);
    if (fromCurrent) return fromCurrent;

    const tags = document.querySelectorAll ? document.querySelectorAll("script[src]") : [];
    for (let i = 0; i < tags.length; i++) {
      const prefix = prefixOf(tags[i].src);
      if (prefix) return prefix;
    }

    return "";
  }

  /**
   * `<read-aloud node="..." for="#selector" voice="..." base="...">`.
   *
   * Reads `node` (the published content key the server routes are keyed on), `voice` (an optional
   * voice, honoured only if the site's configuration allows it), `for` (a selector to an element
   * whose words get highlighted as they are spoken, and whose text is read as a fallback by
   * `speechSynthesis` if the server route is unavailable) and `base` (an optional path prefix for a
   * site that is not mounted at the root).
   *
   * Does nothing over the network until the button inside it is pressed. Several of these can sit
   * on one page with no cost beyond the capability check in `connectedCallback`.
   */
  class ReadAloudElement extends HTMLElement {
    connectedCallback() {
      // Every connect, including a reconnect after removal, marks the element active again. This
      // must run before the idempotency guard below: a DOM move, a tab switch, an accordion, or a
      // framework re-render calls disconnectedCallback (which sets this false) and then
      // connectedCallback again, and if this stayed false a press would render "loading" and then
      // bail at the first _active check forever, an inert button with no error and no recovery.
      this._active = true;

      // Re-insertion into the document calls this again; rebuilding would duplicate the button
      // and drop the audio element's wiring, so a second call is a no-op beyond the line above.
      if (this._button) return;

      const hasFetch = typeof fetch === "function";
      const hasSpeech = typeof window !== "undefined" && "speechSynthesis" in window;

      // A button that cannot ever play is worse than no button.
      if (!hasFetch && !hasSpeech) {
        this.remove();
        return;
      }

      this._nodeKey = this.getAttribute("node");
      if (!this._nodeKey) {
        this.remove();
        return;
      }

      this._base = this.getAttribute("base") || detectBase() || "";
      this._voice = this.getAttribute("voice") || null;
      this._highlighter = null;
      this._boundaryMap = null;
      this._audioEl = null;
      this._mode = null; // "audio" | "speech" | null
      this._state = "idle";
      this._wordIndex = -1;
      this._boundaries = [];
      this._playAbort = null;
      this._utterance = null;
      // Private, not the public data-state attribute: an editor or another script writing
      // data-state="degraded" onto the element in markup must not be able to disable the real
      // server route by accident.
      this._degraded = false;
      // True from the moment speak() is called until onend/onerror, independent of _state, which
      // only becomes "playing" once the browser fires onstart, sometimes considerably later.
      this._speechActive = false;

      injectButtonStyle();

      this._button = document.createElement("button");
      this._button.type = "button";
      this._button.className = "read-aloud-btn";

      this._icon = document.createElement("span");
      this._icon.className = "read-aloud-btn__icon";
      // Package-authored SVG markup, never server text: innerHTML is safe here.
      this._icon.innerHTML = ICONS.play;

      this._label = document.createElement("span");
      this._label.className = "read-aloud-btn__text";
      this._label.textContent = this.getAttribute("label") || "Listen";

      this._button.appendChild(this._icon);
      this._button.appendChild(this._label);
      this.appendChild(this._button);

      // A separate live region for state announcements. Putting aria-live on the button itself
      // means the button's own changing aria-label re-announces the whole control on every press,
      // including transient states like "Loading...".
      this._status = document.createElement("span");
      this._status.className = "read-aloud-sr-only";
      this._status.setAttribute("aria-live", "polite");
      this.appendChild(this._status);

      this._render("idle");
      this._onClick = () => this._toggle();
      this._button.addEventListener("click", this._onClick);
    }

    disconnectedCallback() {
      // In a real browser this is already false by the time the browser calls us. Set it
      // explicitly rather than relying on that: it is what every in-flight `await` in `_play()`
      // checks to decide whether to keep going, and a caller driving this method directly (a test,
      // or framework code that reuses custom element lifecycle hooks outside the DOM) must not be
      // able to leave a stale continuation running.
      this._active = false;
      if (this._playAbort) {
        this._playAbort.abort();
        this._playAbort = null;
      }
      if (this._audioEl) {
        this._audioEl.pause();
        this._audioEl.removeAttribute("src");
        this._audioEl = null;
      }
      if (this._highlighter) {
        this._highlighter.destroy();
        this._highlighter = null;
      }
      // Only cancel the shared speech queue if this element actually owns something in it. A
      // global cancel() here for an element that never spoke (e.g. it 404ed) would silence any
      // other element's fallback that happens to be reading at the same moment.
      if (this._mode === "speech" && typeof window !== "undefined" && window.speechSynthesis) {
        window.speechSynthesis.cancel();
      }
      this._mode = null;
    }

    async _toggle() {
      if (this._state === "playing") {
        this._pause();
        return;
      }
      if (this._state === "paused") {
        if (this._mode === "audio" && this._audioEl) {
          try {
            await this._audioEl.play();
          } catch (err) {
            this._degrade();
          }
        } else if (this._mode === "speech" && typeof window !== "undefined" && window.speechSynthesis) {
          window.speechSynthesis.resume();
          this._render("playing");
        }
        return;
      }
      if (this._degraded) {
        // The server route already failed once this session; go straight to the fallback rather
        // than re-probing an endpoint that just refused.
        this._speak();
        return;
      }
      await this._play();
    }

    _pause() {
      if (this._mode === "audio" && this._audioEl) {
        this._audioEl.pause();
        return;
      }
      if (this._mode === "speech" && typeof window !== "undefined" && window.speechSynthesis) {
        window.speechSynthesis.pause();
        this._render("paused");
      }
    }

    async _play() {
      this._render("loading");

      if (this._playAbort) this._playAbort.abort();
      const abort = (typeof AbortController !== "undefined") ? new AbortController() : null;
      this._playAbort = abort;

      let response;
      try {
        response = await fetch(this._timingsUrl(), abort ? { signal: abort.signal } : undefined);
      } catch (err) {
        if (!this._active) return;
        this._degrade();
        return;
      }
      if (!this._active) return;

      if (response.status === 404) {
        // The article is not readable at all. Said out loud first: a routing prefix that is not
        // reached, a proxy that does not forward /read-aloud/, and a node the site will not read
        // all arrive here alike, and removing the button is otherwise indistinguishable from the
        // feature never having been installed.
        if (typeof console !== "undefined" && console.warn) {
          console.warn(
            "read-aloud: " + this._timingsUrl() + " answered 404, so the button was removed. If "
            + "the article is readable, check that the site's path prefix reaches the app and set "
            + "the base attribute on <read-aloud> if it does not.",
          );
        }
        this.remove();
        return;
      }

      if (response.status === 429) {
        // Not a failure: do not degrade to browser speech, just say so and let the reader retry.
        this.dataset.state = "throttled";
        this._render("idle");
        return;
      }

      if (response.status === 503 || !response.ok) {
        this._degrade();
        return;
      }

      // A previous attempt may have left the throttled marker; this attempt succeeded.
      delete this.dataset.state;

      let boundaries = [];
      try {
        const parsed = await response.json();
        boundaries = Array.isArray(parsed) ? parsed : [];
      } catch (err) {
        boundaries = [];
      }
      if (!this._active) return;

      this._boundaries = boundaries;
      this._wordIndex = -1;
      this._prepareHighlighting();

      // The probe already confirmed the route is good, so point <audio> straight at the media
      // url. That is the second and last request this press makes; the browser streams it itself
      // rather than this code buffering a blob.
      this._setupAudio(this._audioUrl());

      try {
        await this._audioEl.play();
      } catch (err) {
        if (!this._active) return;
        this._degrade();
      }
    }

    _audioUrl() {
      return this._base + "/read-aloud/" + encodeURIComponent(this._nodeKey) + this._voiceQuery();
    }

    _timingsUrl() {
      return this._base + "/read-aloud/" + encodeURIComponent(this._nodeKey) + "/timings" + this._voiceQuery();
    }

    _voiceQuery() {
      return this._voice ? "?voice=" + encodeURIComponent(this._voice) : "";
    }

    _setupAudio(url) {
      if (!this._audioEl) {
        this._audioEl = new Audio();
        this._audioEl.preload = "auto";
        this._audioEl.addEventListener("playing", () => this._render("playing"));
        this._audioEl.addEventListener("pause", () => {
          if (this._mode !== "audio") return;
          if (this._state === "playing" && this._audioEl.currentTime < this._audioEl.duration) {
            this._render("paused");
          }
        });
        this._audioEl.addEventListener("ended", () => {
          this._render("idle");
          if (this._highlighter) this._highlighter.clear();
          this._wordIndex = -1;
        });
        this._audioEl.addEventListener("timeupdate", () => this._onTimeUpdate());
        // A media error after the probe already said 200 is still possible (a rate limit hit on
        // this second request, a transient failure). Treat it exactly like a 503.
        this._audioEl.addEventListener("error", () => this._degrade());
      }
      this._mode = "audio";
      this._audioEl.src = url;
    }

    _resolveTarget() {
      return resolveTarget(this.getAttribute("for"));
    }

    /**
     * Resolved fresh on every press rather than cached at upgrade time: the target may not exist
     * in the DOM yet when this element connects (it renders later, or `for` is set afterward), and
     * caching null forever would leave highlighting and the speech fallback silently disabled.
     */
    _resolveHighlighter() {
      const target = this._resolveTarget();
      if (!target) {
        if (this._highlighter) {
          this._highlighter.destroy();
          this._highlighter = null;
        }
        return null;
      }
      if (this._highlighter && this._highlighter.root === target) return this._highlighter;
      if (this._highlighter) this._highlighter.destroy();
      this._highlighter = new Highlighter(target);
      return this._highlighter;
    }

    /**
     * Builds the boundary-to-span alignment for this press. See `Highlighter.alignBoundaries` for
     * how ordinary tokeniser differences are tolerated and what "genuinely lost" means.
     */
    _prepareHighlighting() {
      const highlighter = this._resolveHighlighter();
      if (!highlighter || !this._boundaries.length) {
        this._boundaryMap = null;
        return;
      }
      this._boundaryMap = highlighter.alignBoundaries(this._boundaries);
    }

    _onTimeUpdate() {
      const map = this._boundaryMap;
      if (!map) return;
      const el = this._audioEl;
      const boundaries = this._boundaries;
      if (!el || !boundaries.length) return;
      const ms = el.currentTime * 1000;
      let idx = this._wordIndex;
      while (idx + 1 < boundaries.length && boundaries[idx + 1].offsetMs <= ms) idx++;
      while (idx >= 0 && boundaries[idx] && boundaries[idx].offsetMs > ms) idx--;
      if (idx !== this._wordIndex && idx >= 0) {
        this._wordIndex = idx;
        const spanIndex = map[idx];
        if (spanIndex >= 0 && this._highlighter) this._highlighter.highlight(spanIndex);
      }
    }

    /** Synthesis is unavailable (503, an unexpected status, a network failure, or a media error). */
    _degrade() {
      // A media error can fire after this element has already been disconnected (pause() does not
      // abort the element's in-flight request, and removeAttribute("src") does not run the load
      // algorithm), and would otherwise start reading the article into a page with no button.
      if (!this._active) return;
      this._degraded = true;
      this.dataset.state = "degraded";
      this._speak();
    }

    _speak() {
      // Already reading via speech, including the window before the browser's onstart event ever
      // fires: a second call (a redundant failure signal, a fast double press) must not restart
      // the utterance from word one. Keyed on a flag set synchronously by this method, not on
      // _state, which only becomes "playing" once onstart actually fires.
      if (this._mode === "speech" && this._speechActive) return;

      if (typeof window === "undefined" || !window.speechSynthesis) {
        // Nothing left that can play. A dead button is worse than no button.
        this.remove();
        return;
      }
      const target = this._resolveTarget();
      const text = target ? target.textContent || "" : "";
      if (!text.trim()) {
        this._render("idle");
        return;
      }
      this._mode = "speech";
      this._speechActive = true;
      const utterance = new SpeechSynthesisUtterance(text);
      this._utterance = utterance;
      utterance.onstart = () => this._render("playing");
      utterance.onend = () => {
        this._speechActive = false;
        this._render("idle");
      };
      utterance.onerror = () => {
        this._speechActive = false;
        this._render("idle");
      };
      // No cancel() here: this element's queue slot is its own, and clearing the shared queue
      // would also drop any other element's pending or speaking utterance.
      window.speechSynthesis.speak(utterance);
      // Rendered immediately rather than waiting for onstart: a browser that silently no-ops
      // speak() (Chrome, called before its voices have loaded, fires neither onstart nor onerror)
      // would otherwise leave the button stuck disabled at "Loading..." forever, exactly the
      // outcome this fallback exists to prevent.
      this._render("playing");
    }

    _render(state) {
      this._state = state;
      if (!this._button) return;
      this._button.dataset.state = state;
      const iconName = state === "loading" ? "loading" : state === "playing" ? "pause" : "play";
      this._icon.innerHTML = ICONS[iconName];
      const label =
        state === "loading" ? "Loading…" :
        state === "playing" ? "Pause" :
        state === "paused" ? "Resume" :
        this.getAttribute("label") || "Listen";
      this._label.textContent = label;
      this._button.setAttribute("aria-label", label);
      this._button.disabled = state === "loading";
      if (this._status) this._status.textContent = label;
    }
  }

  if (typeof customElements !== "undefined" && !customElements.get("read-aloud")) {
    customElements.define("read-aloud", ReadAloudElement);
  }
})();
