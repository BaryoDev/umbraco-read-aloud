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

  /**
   * Derives the site's path base (an IIS virtual application, `UsePathBase`, anything that puts
   * the app under a prefix) from the URL this very script was loaded from, when it was loaded as a
   * classic script. `document.currentScript` is null while a `type="module"` script is executing,
   * so this is a best-effort fallback; the `base` attribute on the element is the reliable path and
   * takes priority whenever it is set.
   */
  function detectBase() {
    if (typeof document === "undefined") return "";
    const script = document.currentScript;
    const src = script && script.src;
    if (!src) return "";
    try {
      const url = new URL(src, document.baseURI || undefined);
      const p = url.pathname;
      if (p.length > SCRIPT_SUFFIX.length && p.slice(p.length - SCRIPT_SUFFIX.length) === SCRIPT_SUFFIX) {
        return p.slice(0, p.length - SCRIPT_SUFFIX.length);
      }
    } catch (err) {
      // Fall through to no base.
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
      // Re-insertion into the document calls this again; rebuilding would duplicate the button
      // and drop the audio element's wiring, so a second call is a no-op.
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

      this._active = true;
      this._base = this.getAttribute("base") || detectBase() || "";
      this._voice = this.getAttribute("voice") || null;
      this._highlighter = null;
      this._highlightAligned = false;
      this._audioEl = null;
      this._mode = null; // "audio" | "speech" | null
      this._state = "idle";
      this._wordIndex = -1;
      this._boundaries = [];
      this._playAbort = null;
      this._utterance = null;

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
      if (this.dataset.state === "degraded") {
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
        // The article is not readable at all.
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
     * Boundaries come from the server's property text; the highlighter's spans come from
     * whitespace-splitting whatever is in the DOM right now, which is not always the same text
     * (a `<code>` block is deliberately skipped, markup can differ from the spoken property). A
     * word-count mismatch is treated as unaligned and highlighting is skipped rather than drifting
     * onto the wrong word for the rest of the article.
     */
    _prepareHighlighting() {
      const highlighter = this._resolveHighlighter();
      if (!highlighter || !this._boundaries.length) {
        this._highlightAligned = false;
        return;
      }
      highlighter.prepare();
      this._highlightAligned = highlighter.wordCount === this._boundaries.length;
    }

    _onTimeUpdate() {
      if (!this._highlightAligned) return;
      const el = this._audioEl;
      const boundaries = this._boundaries;
      if (!el || !boundaries.length) return;
      const ms = el.currentTime * 1000;
      let idx = this._wordIndex;
      while (idx + 1 < boundaries.length && boundaries[idx + 1].offsetMs <= ms) idx++;
      while (idx >= 0 && boundaries[idx] && boundaries[idx].offsetMs > ms) idx--;
      if (idx !== this._wordIndex && idx >= 0) {
        this._wordIndex = idx;
        if (this._highlighter) this._highlighter.highlight(idx);
      }
    }

    /** Synthesis is unavailable (503, an unexpected status, a network failure, or a media error). */
    _degrade() {
      this.dataset.state = "degraded";
      this._speak();
    }

    _speak() {
      // Already reading via speech: a second call (a redundant failure signal, a fast double
      // press) must not restart the utterance from word one.
      if (this._mode === "speech" && (this._state === "playing" || this._state === "paused")) return;

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
      const utterance = new SpeechSynthesisUtterance(text);
      this._utterance = utterance;
      utterance.onstart = () => this._render("playing");
      utterance.onend = () => this._render("idle");
      utterance.onerror = () => this._render("idle");
      // No cancel() here: this element's queue slot is its own, and clearing the shared queue
      // would also drop any other element's pending or speaking utterance.
      window.speechSynthesis.speak(utterance);
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
