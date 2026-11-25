This second experimental Windows release adds local mod packs, Roblox version management, and per-account server rejoin.

- **Mods** imports local ZIP packs with supported cursor and sound assets. Preview packs, enable them, and set their priority in Settings.
- **Roblox Versions** checks named channels, downloads and verifies available Windows Player builds, and lets you pin a build for troubleshooting. Saved launch groups can specify their own channel. Close running Roblox clients before switching builds. Roblox may require a current build for online play.
- **Rejoin last public server** uses the last confirmed public server instance for the linked account. The server must still be active and accessible.
- **Server country** searches more candidates for a matching public server. Using destination `0` opens a filtered Roblox Home window for the linked account.

All 71 regression checks and the Windows Debug and Release dashboard smoke tests passed on the feature branch. The 20 ZIP packages for one historical build matched Roblox's manifest. Live signed-in gameplay, in-game mod effects, and real-world region accuracy remain unverified.

Windows x64 requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Account sign-in requires Microsoft Edge WebView2.
