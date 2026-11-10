using Bloxstrap.AppData;
using Bloxstrap.Integrations;
using Bloxstrap.Models;

namespace Bloxstrap
{
    public class Watcher : IDisposable
    {
        private Mutex? _lock;
        private Mutex? _presenceLock;
        private MultiInstanceLease? _multiLease;
        private readonly PlayerLifetime? _player;
        private Task? _playbackTask;
        private readonly CancellationTokenSource _sessionEnded = new();
        private volatile CancellationTokenSource? _playback;
        private bool _keepAwake;
        private string? _macroOverride;

        public int ProcessId => _watcherData?.ProcessId ?? 0;
        public SessionProfile? Profile
        {
            get
            {
                long linkedUserId = _watcherData?.AccountUserId ?? 0;
                long userId = ActivityWatcher?.InGame == true ? ActivityWatcher.Data.UserId : linkedUserId;
                if (userId <= 0 || (linkedUserId > 0 && linkedUserId != userId))
                    return null;
                return App.Settings.Prop.SessionProfiles.FirstOrDefault(profile => profile.UserId == userId);
            }
        }
        public string? MacroPath => _macroOverride ?? Profile?.MacroPath;
        public bool IsPlaying => _playback is not null;

        private readonly WatcherData? _watcherData;
        
        private readonly NotifyIconWrapper? _notifyIcon;

        public readonly ActivityWatcher? ActivityWatcher;

        public readonly DiscordRichPresence? RichPresence;

        public Watcher()
        {
            const string LOG_IDENT = "Watcher";

            string? watcherDataArg = App.LaunchSettings.WatcherFlag.Data;

            if (String.IsNullOrEmpty(watcherDataArg))
            {
#if DEBUG
                string path = new RobloxPlayerData().ExecutablePath;
                if (!File.Exists(path))
                    throw new ApplicationException("Roblox player is not been installed");

                using var gameClientProcess = Process.Start(path);

                _watcherData = new() { ProcessId = gameClientProcess.Id };
#else
                throw new Exception("Watcher data not specified");
#endif
            }
            else
            {
                _watcherData = JsonSerializer.Deserialize<WatcherData>(Encoding.UTF8.GetString(Convert.FromBase64String(watcherDataArg)));
            }

            if (_watcherData is null)
                throw new Exception("Watcher data is invalid");

            _lock = new Mutex(false, $"{App.ProjectName}-Watcher-{ProcessId}", out bool isNew);
            if (!isNew)
                return;

            _player = PlayerLifetime.Open(_watcherData);
            if (!IsPlayerRunning())
                return;

            if (_watcherData.MultiInstance)
                _multiLease = new MultiInstanceLease();

            _keepAwake = App.Settings.Prop.KeepComputerAwake;
            if (_keepAwake)
                SessionInput.KeepComputerAwake(true);

            if (App.Settings.Prop.EnableActivityTracking ||
                App.Settings.Prop.ForegroundAntiIdle || App.Settings.Prop.SessionProfiles.Count > 0)
            {
                ActivityWatcher = new(_watcherData.LogFile);

                if (App.Settings.Prop.UseDisableAppPatch && !_watcherData.KeepDesktopApp)
                {
                    ActivityWatcher.OnAppClose += delegate
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Received desktop app exit, closing Roblox");
                        if (IsPlayerRunning())
                            CloseProcess(ProcessId);
                    };
                }

                if (App.Settings.Prop.EnableActivityTracking && App.Settings.Prop.UseDiscordRichPresence)
                {
                    _presenceLock = new Mutex(false, $"{App.ProjectName}-RichPresence", out bool ownsPresence);
                    if (ownsPresence)
                        RichPresence = new(ActivityWatcher);
                    else
                    {
                        _presenceLock.Dispose();
                        _presenceLock = null;
                    }
                }
            }

            _notifyIcon = new(this);
            if (IsPlayerRunning())
            {
                using var ready = MultiInstanceLease.CreateReadyEvent(ProcessId);
                ready.Set();
            }
        }

        public void KillRobloxProcess()
        {
            if (IsPlayerRunning())
                CloseProcess(ProcessId, true);
        }

        public void CloseProcess(int pid, bool force = false)
        {
            const string LOG_IDENT = "Watcher::CloseProcess";

            try
            {
                using var process = Process.GetProcessById(pid);

                App.Logger.WriteLine(LOG_IDENT, $"Killing process '{process.ProcessName}' (pid={pid}, force={force})");

                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {pid} has already exited");
                    return;
                }

                if (force)
                    process.Kill();
                else
                    process.CloseMainWindow();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {pid} could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public async Task Run()
        {
            if (_notifyIcon is null || _watcherData is null)
                return;

            Task channel = SessionChannel.ServeAsync(ProcessId, () => new SessionSnapshot(ProcessId,
                _watcherData.ProcessStartTimeUtc, ActivityWatcher?.InGame == true && _watcherData.AccountUserId > 0 && ActivityWatcher.Data.UserId != _watcherData.AccountUserId
                    ? 0 : _watcherData.AccountUserId > 0 ? _watcherData.AccountUserId : ActivityWatcher?.Data.UserId ?? 0,
                ActivityWatcher?.InGame == true, ActivityWatcher?.InGame == true ? ActivityWatcher.Data.PlaceId : 0), _sessionEnded.Token);
            Task exited = _player!.WaitForExitAsync();
            Task activity = TrackActivityAsync();
            Task antiIdle = App.Settings.Prop.ForegroundAntiIdle ? RunAntiIdleAsync() : Task.CompletedTask;
            await exited;
            _sessionEnded.Cancel();

            // Wait for pending input to release its keys before terminating this watcher.
            Task? playback = await App.Current.Dispatcher.InvokeAsync(() => _playbackTask);
            if (playback is not null)
            {
                try { await playback; }
                catch (Exception) { } // The tray command reports playback errors.
            }
            try { await Task.WhenAll(activity, antiIdle, channel); }
            catch (OperationCanceledException) when (_sessionEnded.IsCancellationRequested) { }

            if (_watcherData.AutoclosePids is not null)
            {
                foreach (int pid in _watcherData.AutoclosePids)
                    CloseProcess(pid);
            }

            if (App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Process, "-settings -testmode");
        }

        private bool IsPlayerRunning() => _player?.WaitOne(0) == false;

        private async Task TrackActivityAsync()
        {
            if (ActivityWatcher is null)
                return;

            try
            {
                await ActivityWatcher.RunAsync(_sessionEnded.Token);
            }
            catch (OperationCanceledException) when (_sessionEnded.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteException("Watcher::TrackActivity", ex);
            }
        }

        private async Task RunAntiIdleAsync()
        {
            CancellationToken token = _sessionEnded.Token;
            TimeSpan interval = TimeSpan.FromMinutes(15);
            try
            {
                await Task.Delay(interval, token);
                while (true)
                {
                    bool sent = !IsPlaying && ActivityWatcher?.InGame == true &&
                        await SessionInput.TryPreventIdleAsync(ProcessId, token);
                    await Task.Delay(sent ? interval : TimeSpan.FromSeconds(2), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                App.Logger.WriteException("Watcher::AntiIdle", ex);
            }
        }

        public void SetMacro(string path) => _macroOverride = path;

        public async Task PlayMacro()
        {
            string? path = MacroPath;
            if (_playback is not null || String.IsNullOrWhiteSpace(path))
                return;
            if (_sessionEnded.IsCancellationRequested || !IsPlayerRunning())
                throw new InvalidOperationException("This Roblox instance has closed.");

            using var playbackLock = new Mutex(false, $"{App.ProjectName}-MacroPlayback", out bool canPlay);
            if (!canPlay)
                throw new InvalidOperationException("Another Finstrap client is playing a recording. Stop it first.");

            using var playback = CancellationTokenSource.CreateLinkedTokenSource(_sessionEnded.Token);
            _playback = playback;
            _playbackTask = SessionInput.PlayAsync(ProcessId, path, playback.Token);
            try { await _playbackTask; }
            finally
            {
                _playbackTask = null;
                _playback = null;
            }
        }

        public void StopMacro() => _playback?.Cancel();

        public void Dispose()
        {
            App.Logger.WriteLine("Watcher::Dispose", "Disposing Watcher");

            App.Current.Dispatcher.Invoke(() =>
            {
                if (_keepAwake)
                    SessionInput.KeepComputerAwake(false);
                _notifyIcon?.Dispose();
            });
            RichPresence?.Dispose();
            _presenceLock?.Dispose();
            _multiLease?.Dispose();
            _player?.Dispose();
            _lock?.Dispose();
            _sessionEnded.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
