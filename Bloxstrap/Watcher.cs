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
        private readonly CancellationTokenSource _sessionEnded = new();
        private volatile CancellationTokenSource? _playback;
        private bool _keepAwake;
        private string? _macroOverride;

        public int ProcessId => _watcherData?.ProcessId ?? 0;
        public SessionProfile? Profile => ActivityWatcher?.InGame == true
            ? App.Settings.Prop.SessionProfiles.FirstOrDefault(profile => profile.UserId > 0 && profile.UserId == ActivityWatcher.Data.UserId)
            : null;
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
            if (!isNew || !IsPlayerRunning())
                return;

            if (_watcherData.MultiInstance)
            {
                _multiLease = new MultiInstanceLease();
                using var ready = MultiInstanceLease.CreateReadyEvent(ProcessId);
                ready.Set();
            }

            _keepAwake = App.Settings.Prop.KeepComputerAwake;
            if (_keepAwake)
                SessionInput.KeepComputerAwake(true);

            if (App.Settings.Prop.EnableActivityTracking ||
                App.Settings.Prop.ForegroundAntiIdle || App.Settings.Prop.SessionProfiles.Count > 0)
            {
                ActivityWatcher = new(_watcherData.LogFile);

                if (App.Settings.Prop.UseDisableAppPatch)
                {
                    ActivityWatcher.OnAppClose += delegate
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Received desktop app exit, closing Roblox");
                        using var process = Process.GetProcessById(_watcherData.ProcessId);
                        process.CloseMainWindow();
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
        }

        public void KillRobloxProcess() => CloseProcess(_watcherData!.ProcessId, true);

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

            ActivityWatcher?.Start();
            var interval = Stopwatch.StartNew();
            bool antiIdle = App.Settings.Prop.ForegroundAntiIdle;
            while (IsPlayerRunning())
            {
                if (antiIdle && !IsPlaying && ActivityWatcher?.InGame == true && interval.Elapsed >= TimeSpan.FromMinutes(15))
                {
                    try
                    {
                        if (SessionInput.TryPreventIdle(ProcessId))
                            interval.Restart();
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        App.Logger.WriteException("Watcher::AntiIdle", ex);
                        antiIdle = false;
                    }
                }
                await Task.Delay(1000);
            }
            _sessionEnded.Cancel();

            if (_watcherData.AutoclosePids is not null)
            {
                foreach (int pid in _watcherData.AutoclosePids)
                    CloseProcess(pid);
            }

            if (App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Process, "-settings -testmode");
        }

        private bool IsPlayerRunning()
        {
            if (_watcherData is null)
                return false;

            try
            {
                using var process = Process.GetProcessById(ProcessId);
                return process.ProcessName == App.RobloxPlayerAppName && !process.HasExited &&
                    (_watcherData.ProcessStartTimeUtc == 0 || process.StartTime.ToUniversalTime().Ticks == _watcherData.ProcessStartTimeUtc);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public void SetMacro(string path) => _macroOverride = path;

        public async Task PlayMacro()
        {
            string? path = MacroPath;
            if (_playback is not null || String.IsNullOrWhiteSpace(path))
                return;
            if (!IsPlayerRunning())
                throw new InvalidOperationException("This Roblox instance has closed.");

            using var playbackLock = new Mutex(false, $"{App.ProjectName}-MacroPlayback", out bool canPlay);
            if (!canPlay)
                throw new InvalidOperationException("Another Finstrap client is playing a recording. Stop it first.");

            using var playback = CancellationTokenSource.CreateLinkedTokenSource(_sessionEnded.Token);
            _playback = playback;
            try { await SessionInput.PlayAsync(ProcessId, path, playback.Token); }
            finally { _playback = null; }
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
            _lock?.Dispose();
            _sessionEnded.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
