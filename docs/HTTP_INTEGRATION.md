# HTTP state integration

AudioSwitch can optionally listen for browser or remote state messages and
route them through the same serialized audio-operation queue as process rules.

## Settings

In General settings, enable `HTTP state listener` and choose:

- bind address: `127.0.0.1` for local browser integrations, or `0.0.0.0` for
  other machines on the network;
- port, default `8765`;
- password, sent as `X-AudioSwitch-Password`.

Use a password when binding `0.0.0.0`. The listener does not write the Windows
registry; these values are stored in the normal AudioSwitch XML configuration.

## Message

Send JSON to `POST http://127.0.0.1:8765/api/state`:

```json
{
  "processName": "msedge",
  "address": "https://example.com/game",
  "title": "Example Game",
  "statusText": "playing",
  "state": "switchout"
}
```

`processName` must match the rule's process name. `address`, `title`,
`statusText`, and `state` are optional rule match fields; every non-empty rule
field must be contained in the incoming value, case-insensitively. This allows
one browser process to have separate rules for different pages or states.

Configure a process rule with matching mode `HTTP state`, then set its target
output/spatial/profile action. A matched message returns JSON with `matched`,
`queued`, and the selected rule. The browser can use the endpoint directly
because the listener includes CORS headers and handles `OPTIONS` requests.

## Browser address sub-rules

For a browser-specific page, select the browser process in the Process rules
page, click the link-shaped `Add address rule` button, and add an `Address
sub-rule` under that process. Give it a name such as `Bilibili`, select the
new child row, enter a URL or URL fragment such as `bilibili.com`, and
configure its output/spatial/preset action in the right editor. The address sub-rule uses the same
`processName` and `address` message fields, so it can coexist with several
pages under one browser process.

Address sub-rules are more specific than the normal browser process rule and
win by default. If multiple address fragments match, the longer fragment wins.
An explicit priority override can change this ordering.

Health check:

```text
GET /api/health
```

The HTTP listener is disabled by default. Unsupported routes and malformed
messages do not invoke any audio API.

## Chrome extension

The project includes a ready-to-load Manifest V3 extension in
`chrome-extension/`. It listens for active-tab changes and sends the previous
tab with `state: switchout`, then the new tab with `state: switchin`. It also
sends the current Chrome tab status (`loading` or `complete`) as
`statusText`.

Load the directory from `chrome://extensions` with Developer mode enabled,
then open the extension options to configure the AudioSwitch endpoint and
password. The extension automatically uses process name `chrome` and the
internal state values `switchin` / `switchout`. It requests an origin
permission when a non-loopback endpoint is configured.
