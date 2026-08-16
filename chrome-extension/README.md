# AudioSwitch Browser State extension

This Manifest V3 extension sends browser tab state to AudioSwitch's local HTTP
listener. The process name is detected from the browser automatically. It sends
these fields:

```json
{
  "processName": "msedge",
  "address": "https://www.example.com/",
  "title": "Example",
  "statusText": "complete",
  "state": "switchin"
}
```

When the active tab changes, the previous tab is sent with `state: switchout`
and the new tab with `state: switchin`. Tab loads/title changes send the new
tab again with the current Chrome status.

The extension recognizes common Chromium and Firefox-based browsers, including
Chrome, Edge, Vivaldi, Opera, Brave, Chromium, Firefox, Arc, and Zen.

## Install

1. Enable AudioSwitch's HTTP state listener, normally at
   `http://127.0.0.1:8765/api/state`.
2. Open the browser's extensions page and enable **Developer mode**.
3. Choose **Load unpacked** and select this `chrome-extension` directory.
4. Click the extension icon for the compact status panel, then choose
   **完整设置** to set the endpoint/password and save.

Click `Test` to check the listener health endpoint. This does not require an
HTTP matching rule and does not trigger an audio profile switch.

The extension requests permission for the configured HTTP origin. The default
loopback origins are included in the manifest; a remote listener will produce
an additional Chrome permission prompt.
