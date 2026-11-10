# Finstrap

Finstrap is a Windows fork of [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap). It uses the original settings layout and adds Roblox session controls under **Integrations**.

## Local account command center

The launch menu includes account cards, verified session status, and named launch groups. No AI models or cloud backend are used.

- Use **Add account** to sign in and link an account without starting Roblox. **Reconnect** verifies and refreshes its saved WebView profile. User IDs remain separate from editable profile names.
- Select accounts, use the arrows to set launch order, enter a group name and optional destination, then save. Selecting a group restores its accounts and destination. Launching shows a confirmation preview.
- Launches remain sequential. Each account shows its queue state; **Cancel remaining** leaves current and successful clients running. **Retry failed** excludes clients with verified active watchers. If tracking failed, inspect existing windows before confirming a retry.
- Account cards distinguish a started client from an observed game join. Place IDs are shown when known. Unknown or untracked clients are explicitly labeled. Current-user-only named pipes supply watcher snapshots; the dashboard refreshes status and CPU/memory every five seconds while visible.
- Use **Focus**, **Close game**, or **Tile windows** for verified sessions. Select a group and **Save layout** to remember monitor-relative positions. Group launches restore layouts after windows appear. Missing monitors and out-of-range positions fall back into the available desktop.
- **Export diagnostics** saves an allowlisted summary of runtime, session counts, and queue states. It excludes account identities, configuration, logs, cookies, tickets, and server links.

The first settings write preserves the existing file as `Settings.json.pre-command-center.bak`. Concurrent settings changes are rejected instead of overwriting another window's changes.

## Roblox sessions

- **Multi-instance launching** is experimental. Close existing Roblox Player windows before enabling it, save your settings, then launch each client through Finstrap. Roblox client updates can affect compatibility. If an update is required, Finstrap waits for existing clients to close before updating on the next launch.
- **Keep computer awake** prevents automatic Windows sleep while a watched Roblox Player session is open.
- **15-minute anti-idle preset** sends a Space press after desktop inactivity when the watched Roblox game is in the foreground. It can cause the character to jump. Background clients remain subject to Roblox's [idle disconnect](https://create.roblox.com/docs/reference/engine/classes/Player#Idled).
- **Account profiles** open Roblox in your Windows default browser using separate browser data for each Roblox user ID. Opera, Chrome, Chromium, Brave, Vivaldi, and Firefox are supported. Save your settings, open the profile, sign in once in that window, and click **Play** on Roblox. Finstrap matches the user ID reported by the game to the saved profile and its optional TinyTask recording.
- **TinyTask playback** accepts 32-bit TinyTask .rec files with keyboard input, mouse movement and left, right, or middle mouse clicks. Use the selected client's tray menu to load a recording or play its account's saved recording. Match the game window position used in the recording. Finstrap runs one foreground macro at a time. Press **F12** to stop, or switch away from the target window. TinyTask's executable is separate from Finstrap.

## Launch from saved accounts

Open the Finstrap launch menu and choose **Add account and launch**. Sign in and complete Roblox's verification in the Finstrap window. Finstrap launches automatically once it can verify the account. Use **Launch selected** for checked accounts, or **Launch all** when multi-instance is enabled. One confirmation starts the queue, and Finstrap waits for each client's watcher before continuing.

Use **Destination** to send the batch to one place or server. Leave it blank to use each account's saved place ID. An ID of `0` opens Roblox Home. A positive ID or game link uses Roblox matchmaking. Paste a private-server invite or a link containing a public server instance ID to target that server. Enter a friend's Roblox username or profile link to follow their current game. Roblox still checks access and join permissions for each account.

If Roblox's background tray processes block multi-instance startup, Finstrap closes only those it can verify are tray processes without an open window and retries.

Account launch uses the Microsoft WebView2 Runtime inside Finstrap. Existing browser profiles can still be opened from Integrations. Sign in once in the Finstrap window for each saved account before using its desktop launch session.

## Build on Windows

Install the .NET 10 SDK and Desktop Runtime. From a checkout of the repository:

```powershell
git submodule update --init --recursive
dotnet publish Bloxstrap/Bloxstrap.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output is `Bloxstrap/bin/Release/net10.0-windows/win-x64/publish/Finstrap.exe`. Installing Finstrap registers it as the Roblox launcher for the current Windows user.

Finstrap uses Bloxstrap's [WPF UI fork](https://github.com/bloxstraplabs/wpfui). See the [MIT license](LICENSE) for the original copyright and license terms.
