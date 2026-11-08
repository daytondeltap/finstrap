# Finstrap

Finstrap is a Windows fork of [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap). It uses the original settings layout and adds Roblox session controls under **Integrations**.

## Roblox sessions

- **Multi-instance launching** is experimental. Close existing Roblox Player windows before enabling it, save your settings, then launch each client through Finstrap. Roblox client updates can affect compatibility. If an update is required, Finstrap waits for existing clients to close before updating on the next launch.
- **Keep computer awake** prevents automatic Windows sleep while a watched Roblox Player session is open.
- **15-minute anti-idle preset** sends a Space press after desktop inactivity when the watched Roblox game is in the foreground. It can cause the character to jump. Background clients remain subject to Roblox's [idle disconnect](https://create.roblox.com/docs/reference/engine/classes/Player#Idled).
- **Account profiles** open Roblox in your Windows default browser using separate browser data for each Roblox user ID. Opera, Chrome, Chromium, Brave, Vivaldi, and Firefox are supported. Save your settings, open the profile, sign in once in that window, and click **Play** on Roblox. Finstrap matches the user ID reported by the game to the saved profile and its optional TinyTask recording.
- **TinyTask playback** accepts 32-bit TinyTask .rec files with keyboard input, mouse movement and left, right, or middle mouse clicks. Use the selected client's tray menu to load a recording or play its account's saved recording. Match the game window position used in the recording. Finstrap runs one foreground macro at a time. Press **F12** to stop, or switch away from the target window. TinyTask's executable is separate from Finstrap.

## Launch from saved accounts

Open the Finstrap launch menu and choose **Add account and launch**. Sign in on the Roblox page in the Finstrap window. Once Roblox verifies the sign-in, Finstrap opens the desktop app at Home without needing a place ID. For an account already listed, check it and choose **Launch selected**. When multi-instance launching is enabled, one confirmation queues all checked accounts. Finstrap starts the clients in order so it can match each client to its account.

Account launch uses the Microsoft WebView2 Runtime inside Finstrap. Existing browser profiles can still be opened from Integrations. Sign in once in the Finstrap window for each saved account before using its desktop launch session.

## Build on Windows

Install the .NET 6 SDK and Desktop Runtime. From a checkout of the repository:

```powershell
git submodule update --init --recursive
dotnet publish Bloxstrap/Bloxstrap.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output is `Bloxstrap/bin/Release/net6.0-windows/win-x64/publish/Finstrap.exe`. Installing Finstrap registers it as the Roblox launcher for the current Windows user.

Finstrap uses Bloxstrap's [WPF UI fork](https://github.com/bloxstraplabs/wpfui). See the [MIT license](LICENSE) for the original copyright and license terms.
