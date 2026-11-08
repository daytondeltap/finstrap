# Finstrap

Finstrap is a Windows fork of [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap). It uses the original settings layout and adds Roblox session controls under **Integrations**.

## Roblox sessions

- **Multi-instance launching** is experimental. Close existing Roblox Player windows before enabling it, save your settings, then launch each client through Finstrap. Roblox client updates can affect compatibility. If an update is required, Finstrap waits for existing clients to close before updating on the next launch.
- **Keep computer awake** prevents automatic Windows sleep while a watched Roblox Player session is open.
- **15-minute anti-idle preset** sends a Space press after desktop inactivity when the watched Roblox game is in the foreground. It can cause the character to jump. Background clients remain subject to Roblox's [idle disconnect](https://create.roblox.com/docs/reference/engine/classes/Player#Idled).
- **Account profiles** open Roblox in separate Microsoft Edge sessions keyed to each Roblox user ID. Save your settings before opening a profile, sign in to the corresponding account in Edge, and click **Play** on the Roblox website. Finstrap matches the user ID reported by the game to the saved profile and its optional TinyTask recording.
- **TinyTask playback** accepts 32-bit TinyTask .rec files with keyboard input, mouse movement and left, right, or middle mouse clicks. Use the selected client's tray menu to load a recording or play its account's saved recording. Match the game window position used in the recording. Finstrap runs one foreground macro at a time. Press **F12** to stop, or switch away from the target window. TinyTask's executable is separate from Finstrap.

## Build on Windows

Install the .NET 6 SDK and Desktop Runtime. From a checkout of the repository:

```powershell
git submodule update --init --recursive
dotnet publish Bloxstrap/Bloxstrap.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output is `Bloxstrap/bin/Release/net6.0-windows/win-x64/publish/Finstrap.exe`. Installing Finstrap registers it as the Roblox launcher for the current Windows user.

Finstrap uses Bloxstrap's [WPF UI fork](https://github.com/bloxstraplabs/wpfui). See the [MIT license](LICENSE) for the original copyright and license terms.
