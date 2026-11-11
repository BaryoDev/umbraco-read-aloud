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

const VOICES = { azure: "Azure Speech", browser: "the browser voice" };

// The template is deployed from main, so the button works once the file is published there.
const TEMPLATE_URL = "https://raw.githubusercontent.com/BaryoDev/umbraco-read-aloud/main/azure/speech-resource.json";
const DEPLOY_URL = "https://portal.azure.com/#create/Microsoft.Template/uri/" + encodeURIComponent(TEMPLATE_URL);
const RESOURCES_URL = "https://portal.azure.com/#browse/Microsoft.CognitiveServices%2Faccounts";

// Azure Speech regions as of this release. "Other" takes any region id not listed.
const REGIONS = [
  ["australiaeast", "Australia East"], ["brazilsouth", "Brazil South"],
  ["canadacentral", "Canada Central"], ["centralindia", "Central India"],
  ["centralus", "Central US"], ["eastasia", "East Asia"], ["eastus", "East US"],
  ["eastus2", "East US 2"], ["francecentral", "France Central"],
  ["germanywestcentral", "Germany West Central"], ["japaneast", "Japan East"],
  ["japanwest", "Japan West"], ["jioindiawest", "Jio India West"],
  ["koreacentral", "Korea Central"], ["northcentralus", "North Central US"],
  ["northeurope", "North Europe"], ["norwayeast", "Norway East"],
  ["qatarcentral", "Qatar Central"], ["southafricanorth", "South Africa North"],
  ["southcentralus", "South Central US"], ["southeastasia", "Southeast Asia"],
  ["swedencentral", "Sweden Central"], ["switzerlandnorth", "Switzerland North"],
  ["switzerlandwest", "Switzerland West"], ["uaenorth", "UAE North"],
  ["uksouth", "UK South"], ["westcentralus", "West Central US"],
  ["westeurope", "West Europe"], ["westus", "West US"], ["westus2", "West US 2"],
  ["westus3", "West US 3"],
];
const OTHER = "__other";

const AZ_CREATE =
  "az cognitiveservices account create --name <name> --resource-group <rg> --kind SpeechServices --sku F0 --location <region> --yes";
const AZ_KEY =
  "az cognitiveservices account keys list --name <name> --resource-group <rg> --query key1 -o tsv";

class BaryoDevReadAloudDashboard extends Base {
  #status = null;
  #state = "loading"; // loading | ready | error
  #error = "";
  #busy = false;
  #notice = null; // { ok, text }
  #region = "";
  #voices = null; // { available, count, region, configured: [{ name, inRegion }] }
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
      this.#loadVoices();
    } catch (err) {
      this.#error = err && err.message ? err.message : "Could not load the settings.";
      this.#state = "error";
    }

    this.#render();
  }

  // The list is a nice to have. If it cannot be loaded the dashboard just leaves it out.
  async #loadVoices() {
    try {
      this.#voices = await this.#request("GET", "/voices");
    } catch {
      this.#voices = null;
    }
    if (this.#busy) return;
    // Keep what was typed while the list loaded.
    if (this.querySelector("#ra-key")) this.#region = this.#values().region;
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
    const picked = this.querySelector("#ra-region")?.value ?? "";
    const region = picked === OTHER ? (this.querySelector("#ra-region-other")?.value ?? "") : picked;
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
        await this.#refresh(result.message);
      }
    });

  #remove = () => {
    if (!confirm("Remove the saved key? Readers get the browser voice from the next request.")) return;
    return this.#run(async () => {
      const result = await this.#request("DELETE", "");
      this.#notice = { ok: !!result.ok, text: result.message };
      this.#keyDraft = "";
      await this.#refresh(result.message);
      this.#region = this.#status?.region ?? "";
    });
  };

  // The change is already made when this runs. If reading the status back fails, the notice keeps
  // saying what happened and only adds that the page is out of date.
  async #refresh(done) {
    try {
      this.#status = await this.#request("GET", "");
      this.#loadVoices();
    } catch {
      this.#notice = { ok: true, text: `${done} The status on this page could not be refreshed, reload it to see the current state.` };
    }
  }

  #render() {
    this.innerHTML = `
      <style>
        .ra-grid { display: grid; gap: var(--uui-size-space-4, 12px); max-width: 560px; }
        .ra-field { display: grid; gap: 4px; }
        .ra-field uui-input { width: 100%; }
        .ra-note { color: var(--uui-color-text-alt, #68676b); font-size: 13px; }
        .ra-row { display: flex; gap: 8px; flex-wrap: wrap; }
        .ra-setup summary { cursor: pointer; font-weight: 600; margin-bottom: var(--uui-size-space-3, 9px); }
        .ra-steps { margin: 0; padding-left: 20px; display: grid; gap: 4px; font-size: 14px; }
        .ra-cmd { display: flex; gap: 8px; align-items: flex-start; }
        .ra-cmd code { flex: 1; overflow-wrap: anywhere; font-size: 12px; padding: 6px 8px;
                       background: var(--uui-color-surface-alt, #f3f3f5); border-radius: 3px; }
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

  async #copy(text, button) {
    let done = false;
    try {
      await navigator.clipboard.writeText(text);
      done = true;
    } catch {
      const box = document.createElement("textarea");
      box.value = text;
      box.style.position = "fixed";
      box.style.opacity = "0";
      document.body.append(box);
      box.select();
      try {
        done = document.execCommand("copy");
      } catch {
        done = false;
      }
      box.remove();
    }
    button.setAttribute("label", done ? "Copied" : "Press Ctrl+C to copy");
    setTimeout(() => button.setAttribute("label", "Copy"), 2000);
  }

  #command(text) {
    const row = document.createElement("div");
    row.className = "ra-cmd";
    const code = document.createElement("code");
    code.textContent = text;
    const button = document.createElement("uui-button");
    button.setAttribute("look", "secondary");
    button.setAttribute("compact", "");
    button.setAttribute("label", "Copy");
    button.addEventListener("click", () => this.#copy(text, button));
    row.append(code, button);
    return row;
  }

  #setup() {
    const box = document.createElement("details");
    box.className = "ra-setup";
    box.open = !this.#status.keySaved && !this.#status.unreadable;
    box.innerHTML = `
      <summary>Need a key? Create your own Speech resource</summary>
      <div class="ra-grid">
        <p class="ra-note">The free F0 tier is enough for most sites. You own the resource and the key.</p>
        <div class="ra-row">
          <uui-button look="primary" href="${DEPLOY_URL}" target="_blank" rel="noopener noreferrer"
                      label="Create a Speech resource in Azure"></uui-button>
        </div>
        <ol class="ra-steps">
          <li>Create the resource with the button above and wait for the deployment to finish.</li>
          <li>Open <a href="${RESOURCES_URL}" target="_blank" rel="noopener noreferrer">your Azure AI services</a>
              and open the new resource.</li>
          <li>Go to Keys and Endpoint. Copy Key 1 and Location.</li>
          <li>Pick that Location below, paste the key, press Test, then Save.</li>
        </ol>
        <p class="ra-note">Or use the Azure CLI. Replace the names in angle brackets.</p>
        <div id="ra-cmds" class="ra-grid"></div>
      </div>`;
    box.querySelector("#ra-cmds").append(this.#command(AZ_CREATE), this.#command(AZ_KEY));
    return box;
  }

  #voiceSection() {
    const v = this.#voices;
    const s = this.#status;
    if (!v || (!s.keySaved && !s.unreadable)) return null;

    const box = document.createElement("div");
    box.className = "ra-grid";

    const head = document.createElement("p");
    head.className = "ra-note";
    head.textContent = v.available
      ? `${v.count} voices available in ${v.region}.`
      : "The voice list could not be loaded, so the configured voices are not checked.";
    box.append(head);

    for (const voice of v.configured ?? []) {
      const row = document.createElement("div");
      row.className = "ra-row";
      const name = document.createElement("code");
      name.textContent = voice.name;
      row.append(name);
      if (voice.inRegion !== null && voice.inRegion !== undefined) {
        const tag = document.createElement("uui-tag");
        tag.setAttribute("color", voice.inRegion ? "positive" : "danger");
        tag.textContent = voice.inRegion ? "available" : `not available in ${v.region}`;
        row.append(tag);
      }
      box.append(row);
    }
    return box;
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
    } else if (s.configKeyIgnored) {
      line = ["warn", "A key is in configuration but is not used, because Provider is not set to Azure. Saving a key here switches readers to Azure."];
    } else {
      line = ["warn", "No key is set."];
    }
    body.append(this.#banner(line[0], line[1]));

    const voice = document.createElement("p");
    voice.className = "ra-note";
    voice.textContent = `Readers get ${VOICES[s.voice] ?? s.voice} now.`;
    body.append(voice);

    body.append(this.#setup());

    const form = document.createElement("div");
    form.className = "ra-grid";
    form.innerHTML = `
      <div class="ra-field">
        <uui-label for="ra-region">Region</uui-label>
        <uui-select id="ra-region" label="Region"></uui-select>
        <uui-input id="ra-region-other" label="Other region" placeholder="for example southeastasia"
                   autocomplete="off" hidden></uui-input>
        <span class="ra-note">Use the Location shown under Keys and Endpoint in the portal.</span>
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

    const select = form.querySelector("#ra-region");
    const other = form.querySelector("#ra-region-other");
    const known = REGIONS.some(([id]) => id === this.#region);
    const showOther = this.#region !== "" && !known;
    select.options = [
      { name: "Choose a region", value: "", selected: this.#region === "" },
      ...REGIONS.map(([id, label]) => ({ name: `${label} (${id})`, value: id, selected: id === this.#region })),
      { name: "Other", value: OTHER, selected: showOther },
    ];
    other.value = showOther ? this.#region : "";
    other.hidden = !showOther;
    select.addEventListener("change", () => {
      other.hidden = select.value !== OTHER;
    });
    form.querySelector("#ra-key").value = this.#keyDraft;
    form.querySelector("#ra-key-note").textContent = s.keySaved
      ? "Leave blank to keep the saved key. The key is never shown."
      : "The key is encrypted when saved and never shown again.";

    const locked = s.lockedByConfig;
    const saveButton = form.querySelector("#ra-save");
    const removeButton = form.querySelector("#ra-remove");
    const testButton = form.querySelector("#ra-test");
    if (locked) {
      select.setAttribute("disabled", "");
      other.setAttribute("disabled", "");
      saveButton.setAttribute("disabled", "");
      removeButton.setAttribute("disabled", "");
    }
    if ((!s.keySaved && !s.unreadable) || locked) removeButton.setAttribute("disabled", "");

    testButton.addEventListener("click", this.#test);
    saveButton.addEventListener("click", this.#save);
    removeButton.addEventListener("click", this.#remove);

    const voices = this.#voiceSection();
    if (voices) body.append(voices);

    if (this.#notice) body.append(this.#banner(this.#notice.ok ? "ok" : "bad", this.#notice.text));
  }
}

customElements.define("baryodev-read-aloud-dashboard", BaryoDevReadAloudDashboard);
export default BaryoDevReadAloudDashboard;
