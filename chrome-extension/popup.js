const enabled = document.querySelector("#enabled");
const endpoint = document.querySelector("#endpoint");
const status = document.querySelector("#status");

function showStatus(message, isError = false) {
  status.textContent = message;
  status.style.color = isError ? "#b3261e" : "";
}

async function load() {
  const settings = await chrome.storage.local.get({
    enabled: true,
    endpoint: "http://127.0.0.1:8765/api/state"
  });
  enabled.checked = Boolean(settings.enabled);
  endpoint.textContent = settings.endpoint;
}

enabled.addEventListener("change", async () => {
  await chrome.storage.local.set({ enabled: enabled.checked });
  await chrome.runtime.sendMessage({ type: "settingsChanged", settings: { enabled: enabled.checked } });
  showStatus(enabled.checked ? "已启用" : "已停用");
});

document.querySelector("#options").addEventListener("click", () => {
  chrome.runtime.openOptionsPage();
});

document.querySelector("#test").addEventListener("click", async () => {
  showStatus("测试中…");
  try {
    const result = await chrome.runtime.sendMessage({ type: "test" });
    showStatus(result?.ok ? `成功（HTTP ${result.status || 200}）` : `失败（${result?.status || "不可达"}）`, !result?.ok);
  } catch (error) {
    showStatus(error.message || String(error), true);
  }
});

load().catch((error) => showStatus(error.message || String(error), true));
