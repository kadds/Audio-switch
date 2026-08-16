const DEFAULT_SETTINGS = Object.freeze({
  enabled: true,
  endpoint: "http://127.0.0.1:8765/api/state",
  password: ""
});
const SWITCH_IN_STATE = "switchin";
const SWITCH_OUT_STATE = "switchout";

function detectProcessName() {
  const userAgent = String(globalThis.navigator?.userAgent || "").toLowerCase();
  const brands = (globalThis.navigator?.userAgentData?.brands || [])
    .map((brand) => String(brand.brand || "").toLowerCase())
    .join(" ");
  const browserInfo = `${userAgent} ${brands}`;
  if (browserInfo.includes("edg/") || browserInfo.includes("edge")) return "msedge";
  if (browserInfo.includes("vivaldi")) return "vivaldi";
  if (browserInfo.includes("opr/") || browserInfo.includes("opera")) return "opera";
  if (browserInfo.includes("yabrowser")) return "yandex";
  if (browserInfo.includes("firefox")) return "firefox";
  if (browserInfo.includes("waterfox")) return "waterfox";
  if (browserInfo.includes("librewolf")) return "librewolf";
  if (browserInfo.includes("floorp")) return "floorp";
  if (browserInfo.includes("arc/")) return "arc";
  if (browserInfo.includes("zen")) return "zen";
  if (browserInfo.includes("brave") || globalThis.navigator?.brave) return "brave";
  if (browserInfo.includes("chromium")) return "chromium";
  return "chrome";
}

const PROCESS_NAME = detectProcessName();

let settings = { ...DEFAULT_SETTINGS };
const activeTabByWindow = new Map();
const tabSnapshots = new Map();
const lastSentKeys = new Map();

function mergeSettings(value) {
  return {
    ...DEFAULT_SETTINGS,
    ...(value || {}),
    endpoint: String(value?.endpoint || DEFAULT_SETTINGS.endpoint).trim(),
    password: String(value?.password || "")
  };
}

async function loadSettings() {
  const stored = await chrome.storage.local.get(DEFAULT_SETTINGS);
  settings = mergeSettings(stored);
  return settings;
}

function isWebUrl(address) {
  return typeof address === "string" && /^https?:\/\//i.test(address);
}

function snapshotForTab(tab) {
  if (!tab || typeof tab.id !== "number") return null;
  const previous = tabSnapshots.get(tab.id) || {};
  const snapshot = {
    id: tab.id,
    url: typeof tab.url === "string" ? tab.url : previous.url || "",
    title: typeof tab.title === "string" ? tab.title : previous.title || "",
    status: typeof tab.status === "string" ? tab.status : previous.status || "complete"
  };
  tabSnapshots.set(tab.id, snapshot);
  return snapshot;
}

function statePayload(tab, state, statusTextOverride) {
  const snapshot = snapshotForTab(tab);
  if (!snapshot || !isWebUrl(snapshot.url)) return null;
  return {
    processName: PROCESS_NAME,
    address: snapshot.url,
    title: snapshot.title,
    statusText: statusTextOverride || snapshot.status || "complete",
    state
  };
}

async function sendState(tab, state, statusTextOverride) {
  if (!settings.enabled || !settings.endpoint) return { ok: false, skipped: true };
  const payload = statePayload(tab, state, statusTextOverride);
  if (!payload) return { ok: false, skipped: true };

  const key = JSON.stringify(payload);
  const timestampKey = `${payload.address}\u001f${state}`;
  const now = Date.now();
  const lastKey = lastSentKeys.get(timestampKey);
  if (lastKey?.payload === key && now - lastKey.time < 250) {
    return { ok: true, duplicate: true };
  }
  lastSentKeys.set(timestampKey, { payload: key, time: now });

  const headers = { "Content-Type": "application/json" };
  if (settings.password) headers["X-AudioSwitch-Password"] = settings.password;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const response = await fetch(settings.endpoint, {
      method: "POST",
      mode: "cors",
      credentials: "omit",
      headers,
      body: JSON.stringify(payload),
      signal: controller.signal
    });
    return { ok: response.ok, status: response.status };
  } catch (error) {
    console.debug("AudioSwitch HTTP state delivery failed", error);
    return { ok: false, error: String(error) };
  } finally {
    clearTimeout(timeout);
  }
}

function healthEndpoint(endpoint) {
  const url = new URL(endpoint);
  url.pathname = "/api/health";
  url.search = "";
  url.hash = "";
  return url.toString();
}

async function testEndpoint() {
  if (!settings.endpoint) return { ok: false, error: "HTTP 地址为空" };

  const headers = {};
  if (settings.password) headers["X-AudioSwitch-Password"] = settings.password;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const response = await fetch(healthEndpoint(settings.endpoint), {
      method: "GET",
      mode: "cors",
      credentials: "omit",
      headers,
      signal: controller.signal
    });
    return { ok: response.ok, status: response.status };
  } catch (error) {
    console.debug("AudioSwitch HTTP health check failed", error);
    return { ok: false, error: String(error) };
  } finally {
    clearTimeout(timeout);
  }
}

async function getTab(tabId) {
  try {
    return await chrome.tabs.get(tabId);
  } catch {
    return tabSnapshots.get(tabId) || null;
  }
}

async function sendActiveTab(windowId, state) {
  const tabs = await chrome.tabs.query({ active: true, windowId });
  if (tabs[0]) await sendState(tabs[0], state);
}

chrome.runtime.onInstalled.addListener(async () => {
  const stored = await chrome.storage.local.get(DEFAULT_SETTINGS);
  await chrome.storage.local.set({ ...DEFAULT_SETTINGS, ...stored });
  await loadSettings();
});

chrome.runtime.onStartup.addListener(async () => {
  await loadSettings();
  const windows = await chrome.windows.getAll({ windowTypes: ["normal"] });
  for (const window of windows) {
    if (window.id !== undefined) await sendActiveTab(window.id, SWITCH_IN_STATE);
  }
});

chrome.storage.onChanged.addListener(async (changes, area) => {
  if (area === "local") await loadSettings();
});

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  const previousTabId = activeTabByWindow.get(activeInfo.windowId);
  if (previousTabId !== undefined && previousTabId !== activeInfo.tabId) {
    await sendState(await getTab(previousTabId), SWITCH_OUT_STATE);
  }
  activeTabByWindow.set(activeInfo.windowId, activeInfo.tabId);
  await sendState(await getTab(activeInfo.tabId), SWITCH_IN_STATE);
});

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  snapshotForTab(tab);
  if (!tab.active) return;
  if (changeInfo.status || changeInfo.url || changeInfo.title) {
    activeTabByWindow.set(tab.windowId, tabId);
    await sendState(tab, SWITCH_IN_STATE, changeInfo.status || tab.status);
  }
});

chrome.tabs.onRemoved.addListener(async (tabId, removeInfo) => {
  if (activeTabByWindow.get(removeInfo.windowId) === tabId) {
    await sendState(tabSnapshots.get(tabId), SWITCH_OUT_STATE);
    activeTabByWindow.delete(removeInfo.windowId);
  }
  tabSnapshots.delete(tabId);
});

chrome.windows.onFocusChanged.addListener(async (windowId) => {
  if (windowId !== chrome.windows.WINDOW_ID_NONE) {
    await sendActiveTab(windowId, SWITCH_IN_STATE);
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "settingsChanged") {
    settings = mergeSettings({ ...settings, ...(message.settings || {}) });
    sendResponse({ ok: true });
    return false;
  }

  if (message?.type === "test") {
    (async () => {
      await loadSettings();
      sendResponse(await testEndpoint());
    })();
    return true;
  }

  return false;
});

loadSettings().catch((error) => console.debug("AudioSwitch settings load failed", error));
