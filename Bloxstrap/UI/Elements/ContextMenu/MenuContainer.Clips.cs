using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.ContextMenu
{
    public partial class MenuContainer
    {
        private const int ClipHotkey = 1;
        private WindowReplay? _replay;
        private Mutex? _captureLock;
        private HwndSource? _clipMessageSource;
        private bool _hotkeyRegistered;
        private Task? _savingClip;

        internal async Task FinishClipsAsync()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return;

            Task? stopping = _replay?.StopAsync();
            if (_savingClip is not null)
                await _savingClip;
            if (stopping is not null)
                await stopping;
        }

        private void ClipsMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return;

            bool running = _replay?.IsRunning == true;
            int seconds = _replay?.Seconds ?? 0;
            ClipStatusItem.Header = !running ? "Off"
                : _replay!.IsSaving ? "Saving…"
                : seconds == 0 ? "Buffering or waiting for visible frames"
                : $"Ready · {Math.Min(seconds, 30)} seconds buffered";
            ArmClipsItem.IsEnabled = _replay is null;
            StopClipsItem.IsEnabled = _replay is not null;
            SaveClipItem.IsEnabled = running && seconds > 0 && !_replay!.IsSaving;
            SaveClipItem.InputGestureText = _hotkeyRegistered ? "Ctrl+Shift+F9" : "";
        }

        private void ArmClips_Click(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || _replay is not null)
                return;

            try
            {
                var window = _watcher.GetSessionWindow();
                if (window == IntPtr.Zero)
                    throw new InvalidOperationException("Open this Roblox window before starting the clip buffer.");
                var captureLock = new Mutex(false, "Finstrap-WindowReplay", out bool available);
                if (!available)
                {
                    captureLock.Dispose();
                    throw new InvalidOperationException("Stop capture on the other Roblox window before switching.");
                }

                _captureLock = captureLock;
                _replay = new WindowReplay(window, () =>
                    _activityWatcher?.InGame == true ? _activityWatcher.Data.PlaceId : 0);
                _replay.Failed += message => Dispatcher.BeginInvoke(new Action(() =>
                    Frontend.ShowBalloonTip("Finstrap clips", message)));
                _replay.Start();
                _clipMessageSource = PresentationSource.FromVisual(this) as HwndSource;
                if (_clipMessageSource is not null)
                {
                    _clipMessageSource.AddHook(ClipHotkeyMessage);
                    _hotkeyRegistered = RegisterHotKey(_clipMessageSource.Handle, ClipHotkey, 0x4006, 0x78);
                }
                Frontend.ShowBalloonTip("Finstrap clips", _hotkeyRegistered
                    ? "Buffering silent video. Press Ctrl+Shift+F9 to save."
                    : "Buffering silent video. Use Game clips → Save clip in the tray.");
            }
            catch (Exception ex)
            {
                ReleaseClipControls();
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
            }
        }

        private IntPtr ClipHotkeyMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam == ClipHotkey)
            {
                handled = true;
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && _savingClip is not { IsCompleted: false })
                    _savingClip = SaveClipAsync();
            }
            return IntPtr.Zero;
        }

        private async void SaveClip_Click(object sender, RoutedEventArgs e)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && _savingClip is not { IsCompleted: false })
            {
                _savingClip = SaveClipAsync();
                await _savingClip;
            }
        }

        [SupportedOSPlatform("windows10.0.19041")]
        private async Task SaveClipAsync()
        {
            if (_replay is not { IsSaving: false } replay)
                return;
            try
            {
                string file = await Task.Run(replay.SaveAsync);
                Frontend.ShowBalloonTip("Finstrap clip saved", Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GameClips", ex);
                Frontend.ShowBalloonTip("Finstrap clip could not be saved", ex.Message);
            }
        }

        private async void StopClips_Click(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || _replay is not { } replay)
                return;
            try { await replay.StopAsync(); }
            catch (Exception ex) { App.Logger.WriteException("GameClips", ex); }
            finally { ReleaseClipControls(); }
        }

        [SupportedOSPlatform("windows10.0.19041")]
        private void ReleaseClipControls()
        {
            if (_clipMessageSource is not null)
            {
                if (_hotkeyRegistered)
                    UnregisterHotKey(_clipMessageSource.Handle, ClipHotkey);
                _clipMessageSource.RemoveHook(ClipHotkeyMessage);
                _clipMessageSource = null;
            }
            _hotkeyRegistered = false;
            _replay?.Dispose();
            _replay = null;
            _captureLock?.Dispose();
            _captureLock = null;
        }

        private void OpenClips_Click(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return;
            try
            {
                Directory.CreateDirectory(WindowReplay.Folder);
                Utilities.ShellExecute(WindowReplay.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
