using System.ComponentModel;
using System.Management;

namespace Bloxstrap.Integrations
{
    internal static class RobloxPlayerProcesses
    {
        private static readonly int SessionId = GetCurrentSessionId();

        public static bool HasActivePlayer()
        {
            var players = Process.GetProcessesByName(App.RobloxPlayerAppName);
            try
            {
                if (players.Length == 0)
                    return false;
                var tray = FindTrayProcesses();
                foreach (var player in players)
                {
                    try
                    {
                        if (!player.HasExited && player.SessionId == SessionId && !IsVerifiedTray(player, tray))
                            return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { return true; }
                }
                return false;
            }
            finally
            {
                foreach (var player in players)
                    player.Dispose();
            }
        }

        public static bool CloseVerifiedTrayProcesses()
        {
            var tray = FindTrayProcesses();
            bool closed = false;
            foreach (int pid in tray.Keys)
            {
                try
                {
                    using var player = Process.GetProcessById(pid);
                    if (player.HasExited || player.SessionId != SessionId ||
                        player.ProcessName != App.RobloxPlayerAppName || !IsVerifiedTray(player, tray))
                        continue;
                    player.Kill();
                    closed |= player.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
            return closed;
        }

        private static Dictionary<int, long> FindTrayProcesses()
        {
            var tray = new Dictionary<int, long>();
            try
            {
                // Ask WMI only for tray process identities. Launch arguments can contain tickets.
                using var search = new ManagementObjectSearcher("SELECT ProcessId, CreationDate FROM Win32_Process " +
                    $"WHERE Name = 'RobloxPlayerBeta.exe' AND SessionId = {SessionId} AND CommandLine LIKE '% --launch-to-tray'");
                using var results = search.Get();
                foreach (ManagementObject process in results)
                {
                    using (process)
                    {
                        if (process["ProcessId"] is uint pid && process["CreationDate"] is string created)
                            tray[(int)pid] = ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().Ticks / 10;
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                App.Logger.WriteLine("RobloxPlayerProcesses", "Windows did not confirm the identity of Roblox tray processes.");
            }
            return tray;
        }

        private static bool IsVerifiedTray(Process player, Dictionary<int, long> tray) =>
            tray.TryGetValue(player.Id, out long created) && player.MainWindowHandle == IntPtr.Zero &&
            player.StartTime.ToUniversalTime().Ticks / 10 == created;

        private static int GetCurrentSessionId()
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
    }
}
