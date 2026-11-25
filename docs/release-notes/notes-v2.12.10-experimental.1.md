This experimental Windows release introduces a server location check and a saved country filter for public Roblox servers.

- Choose **Server country** in the Finstrap dashboard. **Check server location** estimates the location before Roblox starts.
- Filtered launches target a specific public server whose estimated country matches. If the search finds no match, Finstrap stops the launch.
- Supported browser Play links open Finstrap with the destination filled in for linked-account selection when a country filter is enabled.
- Choose **Any region** to restore regular matchmaking.

Estimates come from Roblox connection metadata and IP geolocation. They can reflect the relay location. Windows builds, dashboard smoke tests, and regression tests cover the new setting and selection logic. Live authenticated lookup and an actual filtered Roblox join still need validation.

Windows x64 requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Account sign-in requires Microsoft Edge WebView2.
