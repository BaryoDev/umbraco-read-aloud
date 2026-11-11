// Page voice picker, a property editor for the Content section.
//
// A custom element with no static imports and no build step, like the dashboard. It stores the
// voice name, or an empty string for "use the site default". Empty means inherit, never off: to
// switch a page off, use a true/false property (see the README).

const API = "/umbraco/management/api/v1/baryodev/read-aloud/voices";

const Base = await import("@umbraco-cms/backoffice/element-api")
  .then(({ UmbElementMixin }) => UmbElementMixin(HTMLElement))
  .catch(() => HTMLElement);

// The backoffice listens for this event on the element. Imported when it is there, and sent by
// name otherwise, which is the same event.
const ChangeEvent = await import("@umbraco-cms/backoffice/property-editor")
  .then((m) => m.UmbPropertyValueChangeEvent)
  .catch(() => null);

class ReadAloudVoicePicker extends Base {
  constructor() {
    super();
    this._value = "";
    this._voices = [];
    this._defaultVoice = "";
    this._error = "";
    this._root = this.attachShadow({ mode: "open" });
    this._load();
  }

  get value() { return this._value; }
  set value(v) {
    this._value = typeof v === "string" ? v : "";
    this._render();
  }

  // The backoffice passes the data type configuration here. This editor has none.
  set config(_) {}

  async _token() {
    // The backoffice's own auth context, so the call carries the editor's login. Absent outside
    // the backoffice, where the request is simply refused.
    this._auth ??= Promise.all([
      import("@umbraco-cms/backoffice/context-api"),
      import("@umbraco-cms/backoffice/auth"),
    ])
      .then(([{ UmbContextConsumer }, { UMB_AUTH_CONTEXT }]) => {
        const consumer = new UmbContextConsumer(this, UMB_AUTH_CONTEXT);
        const auth = consumer.asPromise();
        consumer.request();
        return auth;
      })
      .catch(() => null);
    const auth = await this._auth;
    return auth ? auth.getLatestToken() : null;
  }

  async _load() {
    try {
      const token = await this._token();
      const response = await fetch(API, {
        credentials: "same-origin",
        headers: token ? { Authorization: "Bearer " + token } : {},
      });
      if (!response.ok) throw new Error(String(response.status));
      const data = await response.json();
      this._voices = Array.isArray(data.voices) ? data.voices : [];
      this._defaultVoice = data.defaultVoice || "";
      this._error = "";
    } catch {
      this._error = "The voice list could not be loaded.";
    }
    this._render();
  }

  _change(e) {
    this._value = e.target.value || "";
    const event = ChangeEvent
      ? new ChangeEvent()
      : new CustomEvent("property-value-change", { bubbles: true, composed: true });
    this.dispatchEvent(event);
  }

  _render() {
    const root = this._root;
    root.replaceChildren();

    const select = document.createElement("uui-select");
    select.setAttribute("label", "Voice for this page");
    select.addEventListener("change", (e) => this._change(e));

    const options = [
      { name: this._defaultVoice ? "Use the site default (" + this._defaultVoice + ")" : "Use the site default", value: "" },
      ...this._voices.map((v) => ({ name: v, value: v })),
    ];
    // A saved voice that is no longer offered stays visible, so the editor sees what is stored.
    if (this._value && !this._voices.includes(this._value)) {
      options.push({ name: this._value + " (not allowed, the site default is used)", value: this._value });
    }
    select.options = options.map((o) => ({ ...o, selected: o.value === this._value }));
    root.appendChild(select);

    if (this._error) {
      const note = document.createElement("small");
      note.textContent = this._error;
      root.appendChild(note);
    }
  }
}

customElements.define("baryodev-read-aloud-voice-picker", ReadAloudVoicePicker);

export default ReadAloudVoicePicker;
