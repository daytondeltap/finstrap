# Finstrap regression checks

Run `dotnet run --project Tests/Finstrap.RegressionTests.csproj` with the .NET 10 SDK.
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

## Command center validation

The console suite also covers launch-group order and layout serialization,
queue cancellation and retry state, monitor-bound clamping, named-pipe snapshots,
and rejection of mismatched process start times. On Linux the .NET 10 mutex tests
must run in an environment that permits native named synchronization objects.

Windows acceptance still required before release:

1. Link two real accounts without launching. Restart, select a saved group and
   confirm the preview, order, destination and profile persistence.
2. Check signed-out/wrong-account flows, cancelling sign-in, cancelling remaining
   launches, a failed watcher handoff, and retrying without duplicating clients.
3. Verify both account cards independently track joins/exits; reopen the dashboard
   to discover existing watchers. Confirm untracked clients are not misattributed.
4. Focus and close individual clients. Save and restore layouts on multiple
   monitors, negative monitor origins, mixed DPI, and after disconnecting a monitor.
5. Edit/remove accounts from both dashboard and Integrations; verify affected
   group references and stale-save warnings. Test unwritable configuration.
6. Export diagnostics and inspect for secrets; verify avatar cache failure does
   not block account management. Test .NET 10 Desktop Runtime and WebView2 setup.
7. Benchmark one, three, and five accounts on the same Windows PC, recording
   time to watcher-ready, dashboard working set, and CPU while visible/hidden.

Live Windows/Roblox results and benchmarks have not been collected in this Linux
workspace. The UI library remains its pinned upstream submodule; its net6 target
is consumed by the net10 application without requiring the .NET 6 runtime.
