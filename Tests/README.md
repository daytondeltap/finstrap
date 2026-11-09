# Finstrap regression checks

Run `dotnet run --project Tests/Finstrap.RegressionTests.csproj` with the .NET 6 SDK.
The console harness compiles the production persistence, account API, destination,
launch-argument, and singleton-lease code. It uses temporary configuration files,
fake HTTP responses, and named synchronization objects; it does not sign in to a
real Roblox account. UI/logging and tray-process discovery are stubbed.
The Windows CI debug workflow runs the same checks, including the Windows-only
named event/mutex collision case.

## Two bug passes

1. Reviewed settings bindings and save reporting, WebView account authentication,
   and the launch queue. Fixed atomic saves, dirty-state tracking, false save
   success, API calls navigating away from login, malformed saved profile keys,
   and the queue continuing after failed starts or watcher handoffs.
2. Reviewed concurrent settings windows, failed account persistence, and watcher
   readiness. Added serialized writes with stale-file detection to avoid losing
   newly linked accounts, preserved unsaved state after failures, blocked launching
   an account whose profile failed to save, and required readiness for single
   account launches too. Added retry bounds and invalid identity regression cases.

## Windows end-to-end checks still required

These require Windows, WebView2, an installed Roblox client, and interactive test
accounts. Cross-compilation and mocked HTTP tests cannot establish live Roblox
compatibility.

- Toggle all three session options, edit a profile, save, close and reopen settings.
  Confirm all edits persist. Edit without saving and confirm the close warning.
- Keep settings open while another launch menu links an account. Save the older
  settings window and confirm it warns rather than deleting the linked account.
- Add an account, complete login/verification, and launch Home and a game. Close
  and reopen Finstrap; launch the saved account without another login. Try an
  expired session, a wrong account, cancelling sign-in, and a private-server link.
- With Roblox closed, enable multi-instance, save, and launch two distinct accounts.
  Confirm both windows stay open and each tray watcher matches its account. Close
  the first window and launch a third. Check the remaining session stays open.
- Simulate watcher startup failure and confirm later queued accounts do not start.
  Confirm normal single-account launching, Studio launch, and disabling
  multi-instance still work.
