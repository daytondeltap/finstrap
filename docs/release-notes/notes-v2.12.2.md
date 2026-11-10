Finstrap's local account command center adds saved launch groups, account cards, separate account linking/reconnect, launch queue controls, verified session status, resource readings, window layouts, and diagnostic export. No AI models or cloud backend are included.

### Changes
- Save account order and a shared destination as a named launch group; preview before launching.
- Add/reconnect an account without starting a game. Launch queues support cancelling remaining accounts and retrying failed accounts.
- Focus or close a verified account's game, tile windows, and save/restore monitor-relative layouts.
- Distinguish started clients from observed game joins; show unknown status when a client cannot be verified.
- Settings writes are atomic, preserve a pre-command-center backup, and reject stale writes from other windows.
- Moves the application to .NET 10. The pinned WPF UI dependency retains its original target framework.

### Requirements
Windows x64, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), and Microsoft Edge WebView2 Runtime for account sign-in. This build is unsigned. Keep the supplied files together when using the ZIP download.

### Validation and limitations
Release regression tests passed locally, and Windows CI builds and regression checks passed. A published-executable Windows dashboard smoke test opens the dashboard, verifies account bindings, saves a group, reorders accounts, exercises queue cancellation, and renders a screenshot.

Live Roblox sign-in, simultaneous Roblox clients, real-game tracking, mixed-DPI/monitor layouts, and one/three/five-account performance benchmarks have not been validated. Multi-instance launching remains experimental. Session/resource status refreshes every five seconds while the dashboard is visible; it does not use persistent event subscriptions. Inspect existing windows before retrying a failed launch whose watcher could not be verified.
