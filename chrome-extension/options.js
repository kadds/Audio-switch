const defaults = {
  enabled: true,
  endpoint: "http://127.0.0.1:8765/api/state",
  password: ""
};

const elements = {
  enabled: document.querySelector("#enabled"),
  endpoint: document.querySelector("#endpoint"),
  password: document.querySelector("#password"),
  save: document.querySelector("#save"),
  test: document.querySelector("#test"),
  status: document.querySelector("#status")
};

function showStatus(message, isError = false) {
  elements.status.textContent = message;
  elements.status.style.color = isError ? "#b3261e" : "";
}

function readForm() {
  return {
    enabled: elements.enabled.checked,
    endpoint: elements.endpoint.value.trim() || defaults.endpoint,
    password: elements.password.value
  };
}

async function requestEndpointPermission(endpoint) {
  const url = new URL(endpoint);
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error("HTTP 地址必须使用 http:// 或 https://");
  }
  const origin = `${url.protocol}//${url.host}/*`;
  const granted = await chrome.permissions.contains({ origins: [origin] });
  if (!granted) {
    const requested = await chrome.permissions.request({ origins: [origin] });
    if (!requested) throw new Error("没有获得该 HTTP 地址的访问权限");
  }
}

async function load() {
  const settings = { ...defaults, ...(await chrome.storage.local.get(defaults)) };
  elements.enabled.checked = Boolean(settings.enabled);
  elements.endpoint.value = settings.endpoint;
  elements.password.value = settings.password;
}

elements.save.addEventListener("click", async () => {
  try {
    const settings = readForm();
    await requestEndpointPermission(settings.endpoint);
    await chrome.storage.local.set(settings);
    await chrome.runtime.sendMessage({ type: "settingsChanged", settings });
    showStatus("已保存。标签页切换时会开始投递状态。", false);
  } catch (error) {
    showStatus(error.message || String(error), true);
  }
});

elements.test.addEventListener("click", async () => {
  showStatus("正在发送测试消息…", false);
  try {
    const result = await chrome.runtime.sendMessage({ type: "test" });
    if (result?.ok) showStatus(`测试成功（HTTP ${result.status || 200}）`, false);
    else showStatus(`测试失败：${result?.error || `HTTP ${result?.status || "不可达"}`}`, true);
  } catch (error) {
    showStatus(error.message || String(error), true);
  }
});

load().catch((error) => showStatus(error.message || String(error), true));
