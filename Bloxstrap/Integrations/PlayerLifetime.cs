using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bloxstrap.Integrations
{
    internal sealed class PlayerLifetime : WaitHandle
    {
        private const uint Synchronize = 0x00100000;

        private PlayerLifetime(SafeWaitHandle handle) => SafeWaitHandle = handle;

        public static PlayerLifetime? Open(WatcherData data)
        {
            // Keep only synchronization access after checking the process identity.
            SafeWaitHandle? handle = OpenProcess(Synchronize, false, data.ProcessId);
            try
            {
                if (handle.IsInvalid)
                    return null;

                using var process = Process.GetProcessById(data.ProcessId);
                if (process.ProcessName != App.RobloxPlayerAppName || process.HasExited ||
                    (data.ProcessStartTimeUtc != 0 && process.StartTime.ToUniversalTime().Ticks != data.ProcessStartTimeUtc))
                    return null;

                var lifetime = new PlayerLifetime(handle);
                handle = null;
                return lifetime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public async Task WaitForExitAsync()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(this,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                completed, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await completed.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(null);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int processId);
    }
}
