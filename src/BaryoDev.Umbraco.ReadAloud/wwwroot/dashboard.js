// Read Aloud dashboard, in the Settings section.
//
// A custom element with no static imports and no build step, as in the PWA package. Umbraco 17
// and 18 call provideContext() on a dashboard as they mount it, which a bare HTMLElement does not
// have, so the base class comes from the backoffice's own import map. Outside the backoffice
// there is none and a plain element is enough.
//
// The key is write-only. It lives in the input box and in the one request that sends it, and the
// server never sends any part of it back.

const API = "/umbraco/management/api/v1/baryodev/read-aloud/azure";

const Base = await import("@umbraco-cms/backoffice/element-api")
  .then(({ UmbElementMixin }) => UmbElementMixin(HTMLElement))
  .catch(() => HTMLElement);

const VOICES = { azure: "Azure Speech", browser: "the browser voice", edge: "Edge (deprecated)" };

class BaryoDevReadAloudDashboard extends Base {
  #status = null;
  #state = "loading"; // loading | ready | error
  #error = "";
  #busy = false;
  #notice = null; // { ok, text }
  #region = "";
  #keyDraft = ""; // held in memory only so a re-render does not empty the box
  #auth = null;

  connectedCallback() {
    super.connectedCallback?.();
    this.#render();
    this.#load();
  }

  async #load() {
    this.#state = "loading";
    this.#render();

    try {
      this.#status = await this.#request("GET", "");
      this.#region = this.#status.region ?? "";
      this.#state = "ready";
    } catch (err) {
      this.#error = err && err.message ? err.message : "Could not load the settings.";
      this.#state = "error";
    }

    this.#render();
  }

  async #request(method, path, body) {
    const token = await this.#token();
    const headers = token ? { Authorization: `Bearer ${token}` } : {};
    if (body !== undefined) headers["Content-Type"] = "application/json";

    const res = await fetch(API + path, {
      method,
      credentials: "same-origin",
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    if (res.status === 401 || res.status === 403) {
      throw new Error("You do not have access to the Settings section.");
    }

    // 400 and 409 carry a { message } written by the server. Anything else is reported by status
    // alone, so no raw response text is ever shown.
    if (res.status === 400 || res.status === 409) {
      const data = await res.json().catch(() => null);
      if (data && typeof data.message === "string") return data;
    }
    if (!res.ok) throw new Error(`Request failed (${res.status}).`);
    return res.json();
  }

  async #token() {
    this.#auth ??= Promise.all([
      import("@umbraco-cms/backoffice/context-api"),
      import("@umbraco-cms/backoffice/auth"),
    ])
      .then(([{ UmbContextConsumer }, { UMB_AUTH_CONTEXT }]) => {
        // asPromise() only waits; request() is what asks the backoffice for the context.
        const consumer = new UmbContextConsumer(this, UMB_AUTH_CONTEXT);
        const auth = consumer.asPromise();
        consumer.request();
        return auth;
      })
      .catch(() => null);
    const auth = await this.#auth;
    return auth ? auth.getLatestToken() : null;
  }

  #values() {
    const key = this.querySelector("#ra-key")?.value ?? "";
    const region = this.querySelector("#ra-region")?.value ?? "";
    this.#keyDraft = key;
    return { region: region.trim(), key: key.trim() };
  }

  async #run(work) {
    this.#busy = true;
    this.#notice = null;
    this.#region = this.#values().region;
    for (const button of this.querySelectorAll("uui-button")) button.setAttribute("disabled", "");
    try {
      await work();
    } catch (err) {
      this.#notice = { ok: false, text: err && err.message ? err.message : "That did not work." };
    }
    this.#busy = false;
    this.#render();
  }

  #test = () =>
    this.#run(async () => {
      const result = await this.#request("POST", "/test", this.#values());
      this.#notice = { ok: !!result.ok, text: result.message };
    });

  #save = () =>
    this.#run(async () => {
      const result = await this.#request("PUT", "", this.#values());
      this.#notice = { ok: !!result.ok, text: result.message };
      if (result.ok) {
        this.#keyDraft = "";
        this.#status = await this.#request("GET", "");
      }
    });

  #remove = () => {
    if (!confirm("Remove the saved key? Readers get the browser voice from the next request.")) return;
    return this.#run(async () => {
      const result = await this.#request("DELETE", "");
      this.#notice = { ok: !!result.ok, text: result.message };
      this.#keyDraft = "";
      this.#status = await this.#request("GET", "");
      this.#region = this.#status.region ?? "";
    });
  };

  #render() {
    this.innerHTML = `
      <style>
        .ra-grid { display: grid; gap: var(--uui-size-space-4, 12px); max-width: 560px; }
        .ra-field { display: grid; gap: 4px; }
        .ra-field uui-input { width: 100%; }
        .ra-note { color: var(--uui-color-text-alt, #68676b); font-size: 13px; }
        .ra-row { display: flex; gap: 8px; flex-wrap: wrap; }
        .ra-banner {
          border-left: 3px solid; border-radius: var(--uui-border-radius, 3px);
          padding: var(--uui-size-space-4, 12px) var(--uui-size-space-5, 16px); font-size: 14px;
        }
        .ra-banner.ok { border-color: var(--uui-color-positive, #2b8e57);
                        background: var(--uui-color-positive-standalone, #eaf5ee); }
        .ra-banner.warn { border-color: var(--uui-color-warning, #fab00f);
                          background: var(--uui-color-warning-standalone, #fdf5e3); }
        .ra-banner.bad { border-color: var(--uui-color-danger, #d42054);
                         background: var(--uui-color-danger-standalone, #fbeaef); }
      </style>
      <uui-box headline="Read Aloud">
        <div class="ra-grid" id="ra-body"></div>
      </uui-box>`;

    const body = this.querySelector("#ra-body");
    this.#fill(body);
  }

  #banner(kind, text) {
    const el = document.createElement("div");
    el.className = `ra-banner ${kind}`;
    el.textContent = text;
    return el;
  }

  #fill(body) {
    if (this.#state === "loading") {
      body.innerHTML = `<uui-loader-bar></uui-loader-bar>`;
      return;
    }

    if (this.#state === "error") {
      body.append(this.#banner("bad", this.#error));
      const retry = document.createElement("uui-button");
      retry.setAttribute("look", "secondary");
      retry.setAttribute("label", "Try again");
      retry.addEventListener("click", () => this.#load());
      body.append(retry);
      return;
    }

    const s = this.#status;

    if (!s.installed) {
      body.append(
        this.#banner(
          "warn",
          "Install the BaryoDev.Umbraco.ReadAloud.Azure package to use Azure Speech. " +
            "Until then readers get the browser voice.",
        ),
      );
      return;
    }

    let line;
    if (s.unreadable) {
      line = ["bad", "Saved key can no longer be read, enter it again. Readers get the browser voice until you do."];
    } else if (s.source === "config") {
      line = ["ok", "The key is set by configuration. Change it there; Save and Remove are off here."];
    } else if (s.source === "dashboard") {
      line = ["ok", "A key is saved. It is encrypted and cannot be shown again."];
    } else {
      line = ["warn", "No key is set."];
    }
    body.append(this.#banner(line[0], line[1]));

    const voice = document.createElement("p");
    voice.className = "ra-note";
    voice.textContent = `Readers get ${VOICES[s.voice] ?? s.voice} now.`;
    body.append(voice);

    const form = document.createElement("div");
    form.className = "ra-grid";
    form.innerHTML = `
      <div class="ra-field">
        <uui-label for="ra-region">Region</uui-label>
        <uui-input id="ra-region" label="Region" placeholder="southeastasia" autocomplete="off"></uui-input>
      </div>
      <div class="ra-field">
        <uui-label for="ra-key">Key</uui-label>
        <uui-input id="ra-key" label="Key" type="password" autocomplete="off"></uui-input>
        <span class="ra-note" id="ra-key-note"></span>
      </div>
      <div class="ra-row">
        <uui-button id="ra-test" look="secondary" label="Test"></uui-button>
        <uui-button id="ra-save" look="primary" color="positive" label="Save"></uui-button>
        <uui-button id="ra-remove" look="secondary" color="danger" label="Remove"></uui-button>
      </div>`;
    body.append(form);

    form.querySelector("#ra-region").value = this.#region;
    form.querySelector("#ra-key").value = this.#keyDraft;
    form.querySelector("#ra-key-note").textContent = s.keySaved
      ? "Leave blank to keep the saved key. The key is never shown."
      : "The key is encrypted when saved and never shown again.";

    const locked = s.lockedByConfig;
    const saveButton = form.querySelector("#ra-save");
    const removeButton = form.querySelector("#ra-remove");
    const testButton = form.querySelector("#ra-test");
    if (locked) {
      form.querySelector("#ra-region").setAttribute("disabled", "");
      saveButton.setAttribute("disabled", "");
      removeButton.setAttribute("disabled", "");
    }
    if ((!s.keySaved && !s.unreadable) || locked) removeButton.setAttribute("disabled", "");

    testButton.addEventListener("click", this.#test);
    saveButton.addEventListener("click", this.#save);
    removeButton.addEventListener("click", this.#remove);

    if (this.#notice) body.append(this.#banner(this.#notice.ok ? "ok" : "bad", this.#notice.text));
  }
}

customElements.define("baryodev-read-aloud-dashboard", BaryoDevReadAloudDashboard);
export default BaryoDevReadAloudDashboard;
