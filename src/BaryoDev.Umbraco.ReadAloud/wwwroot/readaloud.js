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
 * The audio route is always requested first. Hitting /timings on a cold article triggers
 * synthesis itself and blocks for its full duration, the same as the audio route would, so nothing
 * is gained by asking for it first and the audio can start streaming sooner if it is asked for
 * first instead.
 */
(function () {
  "use strict";

  const DEFAULT_SKIP = ["code", "pre", "script", "style", "[data-read-aloud-skip]"];
  const HIGHLIGHT_STYLE_ID = "read-aloud-highlight-style";
  const BUTTON_STYLE_ID = "read-aloud-btn-style";

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
      "@media (prefers-reduced-motion: reduce){.read-aloud-spin{animation:none}}";
    document.head.appendChild(style);
  }

  function resolveTarget(selector) {
    if (!selector) return null;
    return document.querySelector(selector);
  }

  /**
   * `<read-aloud node="..." for="#selector" voice="...">`.
   *
   * Reads `node` (the published content key the server routes are keyed on), `voice` (an optional
   * voice, honoured only if the site's configuration allows it) and `for` (a selector to an element
   * whose words get highlighted as they are spoken, and whose text is read as a fallback by
   * `speechSynthesis` if the server route is unavailable).
   *
   * Does nothing over the network until the button inside it is pressed. Several of these can sit
   * on one page with no cost beyond the capability check in `connectedCallback`.
   */
  class ReadAloudElement extends HTMLElement {
    connectedCallback() {
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

      this._voice = this.getAttribute("voice") || null;
      this._targetEl = resolveTarget(this.getAttribute("for"));
      this._highlighter = this._targetEl ? new Highlighter(this._targetEl) : null;
      this._audioEl = null;
      this._state = "idle";
      this._wordIndex = -1;
      this._boundaries = [];

      injectButtonStyle();

      this._button = document.createElement("button");
      this._button.type = "button";
      this._button.className = "read-aloud-btn";
      this._button.setAttribute("aria-live", "polite");

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

      this._render("idle");
      this._onClick = () => this._toggle();
      this._button.addEventListener("click", this._onClick);
    }

    disconnectedCallback() {
      if (this._audioEl) {
        this._audioEl.pause();
        this._audioEl.removeAttribute("src");
        this._audioEl = null;
      }
      if (this._highlighter) this._highlighter.destroy();
      if (typeof window !== "undefined" && window.speechSynthesis) window.speechSynthesis.cancel();
    }

    async _toggle() {
      if (this._state === "playing") {
        this._pause();
        return;
      }
      if (this._state === "paused" && this._audioEl) {
        try {
          await this._audioEl.play();
        } catch (err) {
          this._degrade();
        }
        return;
      }
      if (this._state === "degraded") {
        this._speak();
        return;
      }
      await this._play();
    }

    _pause() {
      if (this._audioEl) this._audioEl.pause();
      if (typeof window !== "undefined" && window.speechSynthesis) window.speechSynthesis.pause();
    }

    async _play() {
      this._render("loading");

      let response;
      try {
        response = await fetch(this._audioUrl());
      } catch (err) {
        this._degrade();
        return;
      }

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

      // The status is known good. Release this response's body without buffering it into a blob,
      // then point the audio element straight at the URL so the browser streams the file itself.
      if (response.body && typeof response.body.cancel === "function") {
        response.body.cancel().catch(function () {});
      }

      this._setupAudio(response.url || this._audioUrl());
      // Fire and forget: the audio route was already confirmed good, and waiting on timings here
      // would delay the first sound for no benefit. Highlighting starts once they arrive.
      this._loadTimings();

      try {
        await this._audioEl.play();
      } catch (err) {
        this._degrade();
      }
    }

    _audioUrl() {
      return "/read-aloud/" + encodeURIComponent(this._nodeKey) + this._voiceQuery();
    }

    _timingsUrl() {
      return "/read-aloud/" + encodeURIComponent(this._nodeKey) + "/timings" + this._voiceQuery();
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
          if (this._audioEl && this._audioEl.currentTime < this._audioEl.duration) {
            if (this._state === "playing") this._render("paused");
          }
        });
        this._audioEl.addEventListener("ended", () => {
          this._render("idle");
          if (this._highlighter) this._highlighter.clear();
          this._wordIndex = -1;
        });
        this._audioEl.addEventListener("timeupdate", () => this._onTimeUpdate());
        this._audioEl.addEventListener("error", () => this._degrade());
      }
      this._audioEl.src = url;
    }

    async _loadTimings() {
      try {
        const response = await fetch(this._timingsUrl());
        if (!response.ok) return;
        const boundaries = await response.json();
        this._boundaries = Array.isArray(boundaries) ? boundaries : [];
      } catch (err) {
        // Losing timings loses highlighting only; the audio the reader asked for keeps playing.
      }
    }

    _onTimeUpdate() {
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

    /** Synthesis is unavailable (503, an unexpected status, or a network failure). */
    _degrade() {
      this.dataset.state = "degraded";
      this._speak();
    }

    _speak() {
      if (typeof window === "undefined" || !window.speechSynthesis) {
        // Nothing left that can play. A dead button is worse than no button.
        this.remove();
        return;
      }
      const text = this._targetEl ? this._targetEl.textContent || "" : "";
      if (!text.trim()) {
        this._render("idle");
        return;
      }
      const utterance = new SpeechSynthesisUtterance(text);
      utterance.onstart = () => this._render("playing");
      utterance.onend = () => this._render("idle");
      utterance.onerror = () => this._render("idle");
      window.speechSynthesis.cancel();
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
    }
  }

  if (typeof customElements !== "undefined" && !customElements.get("read-aloud")) {
    customElements.define("read-aloud", ReadAloudElement);
  }
})();
