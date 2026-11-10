using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace Bloxstrap.Integrations
{
    internal static class SessionWindows
    {
        private static IntPtr Window(SessionSnapshot s)
        {
            using var p = Process.GetProcessById(s.ProcessId);
            if (p.HasExited || p.ProcessName != App.RobloxPlayerAppName || p.StartTime.ToUniversalTime().Ticks != s.StartedUtc)
                throw new InvalidOperationException("This Roblox session has ended.");
            if (p.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("The game window is not ready yet.");
            return p.MainWindowHandle;
        }
        public static void Focus(SessionSnapshot s)
        {
            var h = Window(s); ShowWindow(h, 9);
            if (!SetForegroundWindow(h)) throw new InvalidOperationException("Windows could not focus this client. Select it from the taskbar.");
        }
        public static void Close(SessionSnapshot s) => PostMessage(Window(s), 0x10, IntPtr.Zero, IntPtr.Zero);
        public static SavedWindowLayout Capture(SessionSnapshot s)
        {
            var h = Window(s);
            if (!GetWindowRect(h, out var r)) throw new InvalidOperationException("Could not read the window position.");
            var monitor = Screen.FromHandle(h); var a = monitor.WorkingArea;
            return new() { Monitor = monitor.DeviceName, X = (r.Left-a.Left)/(double)a.Width,
                Y = (r.Top-a.Top)/(double)a.Height, Width = (r.Right-r.Left)/(double)a.Width, Height = (r.Bottom-r.Top)/(double)a.Height };
        }
        public static void Restore(SessionSnapshot s, SavedWindowLayout layout)
        {
            var h = Window(s);
            var a = (Screen.AllScreens.FirstOrDefault(x => x.DeviceName == layout.Monitor) ?? Screen.PrimaryScreen!).WorkingArea;
            var fit = WindowLayoutMath.Fit(layout.X,layout.Y,layout.Width,layout.Height,a.Left,a.Top,a.Width,a.Height);
            ShowWindow(h,9);
            if (!SetWindowPos(h,IntPtr.Zero,fit.X,fit.Y,fit.Width,fit.Height,0x14)) throw new InvalidOperationException("Could not position the game window.");
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out Rect r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
    }
}
