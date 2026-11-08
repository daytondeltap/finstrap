using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bloxstrap.Integrations
{
    internal static class SessionInput
    {
        private const uint KeyUp = 2;
        private const uint ExtendedKey = 1;
        private static readonly int InputSize = Marshal.SizeOf<Input>();
        private static readonly uint LastInputSize = (uint)Marshal.SizeOf<LastInput>();

        public static IntPtr FocusWindow(int pid)
        {
            using var process = Process.GetProcessById(pid);
            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero)
                throw new InvalidOperationException("Roblox has no visible window yet.");

            if (IsIconic(window))
                ShowWindowAsync(window, 9); // SW_RESTORE
            SetForegroundWindow(window);
            return window;
        }

        public static async Task PlayAsync(int pid, string path, CancellationToken token)
        {
            var steps = await Task.Run(() => TinyTaskRecording.Read(path, token), token);
            token.ThrowIfCancellationRequested();
            IntPtr window = FocusWindow(pid);
            await Task.Run(() => ReplayAsync(window, pid, steps, token), token);
        }

        private static async Task ReplayAsync(IntPtr window, int pid, List<TinyTaskRecording.Step> steps, CancellationToken token)
        {
            var activation = Stopwatch.StartNew();
            while (!IsForeground(window, pid))
            {
                if (activation.ElapsedMilliseconds >= 500)
                    throw new InvalidOperationException("Windows did not activate the selected Roblox window.");
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            var pressedKeys = new HashSet<ushort>();
            var pressedButtons = new HashSet<uint>();
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var step in steps)
                {
                    while (step.Milliseconds > clock.ElapsedMilliseconds)
                    {
                        CheckTarget();
                        await Task.Delay((int)Math.Clamp(step.Milliseconds - clock.ElapsedMilliseconds, 1, 30), token).ConfigureAwait(false);
                    }

                    CheckTarget();
                    if (step.IsKeyboard)
                    {
                        bool down = step.Message is 0x100 or 0x104;
                        Send(Key(step.Key, !down));
                        if (down)
                            pressedKeys.Add(step.Key);
                        else
                            pressedKeys.Remove(step.Key);
                        continue;
                    }

                    // TinyTask uses screen coordinates. Stop before clicking outside the
                    // selected window, including when another window covers that point.
                    GetWindowThreadProcessId(WindowFromPoint(new Point { X = step.First, Y = step.Second }), out uint hitPid);
                    if (hitPid != pid || !SetCursorPos(step.First, step.Second))
                        throw new InvalidOperationException("A recorded mouse position is outside this Roblox window. Match the window size and position used for recording.");

                    CheckTarget();
                    if (step.Message == 0x200)
                        continue;

                    (uint flag, uint release) = step.Message switch
                    {
                        0x201 => (0x02u, 0x04u), 0x202 => (0x04u, 0x04u),
                        0x204 => (0x08u, 0x10u), 0x205 => (0x10u, 0x10u),
                        0x207 => (0x20u, 0x40u), _ => (0x40u, 0x40u)
                    };
                    Send(Mouse(flag));
                    if (flag == release)
                        pressedButtons.Remove(release);
                    else
                        pressedButtons.Add(release);
                }
            }
            finally
            {
                if (pressedKeys.Count > 0 || pressedButtons.Count > 0)
                {
                    Input[] releases = pressedKeys.Select(key => Key(key, true))
                        .Concat(pressedButtons.Select(Mouse)).ToArray();
                    SendInput((uint)releases.Length, releases, InputSize);
                }
            }

            void CheckTarget()
            {
                token.ThrowIfCancellationRequested();
                if (GetAsyncKeyState(0x7b) < 0)
                    throw new OperationCanceledException();
                if (!IsForeground(window, pid))
                    throw new InvalidOperationException("Playback stopped because the selected Roblox window lost focus.");
            }
        }

        public static async Task<bool> TryPreventIdleAsync(int pid, CancellationToken token)
        {
            var info = new LastInput { Size = LastInputSize };
            if (!GetLastInputInfo(ref info) || unchecked((uint)Environment.TickCount - info.Time) < 15 * 60 * 1000)
                return false;

            IntPtr foreground = GetForegroundWindow();
            if (!IsForeground(foreground, pid))
                return false;

            token.ThrowIfCancellationRequested();
            Send(Key(0x20, false));
            try { await Task.Delay(120, token).ConfigureAwait(false); }
            finally { Send(Key(0x20, true)); }
            return true;
        }

        public static void KeepComputerAwake(bool enabled) =>
            SetThreadExecutionState(enabled ? 0x80000001 : 0x80000000);

        private static bool IsForeground(IntPtr window, int pid)
        {
            GetWindowThreadProcessId(window, out uint owner);
            return owner == pid && GetForegroundWindow() == window;
        }

        private static Input Key(ushort key, bool up)
        {
            uint flags = key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
                or 0x2d or 0x2e or 0x6f or 0x90 or 0xa3 or 0xa5 ? ExtendedKey : 0;
            return new Input { Type = 1, Data = new InputData { Key = new Keyboard { VirtualKey = key, Flags = flags | (up ? KeyUp : 0) } } };
        }

        private static Input Mouse(uint flag) =>
            new() { Data = new InputData { Mouse = new MouseInput { Flags = flag } } };

        private static void Send(Input input)
        {
            if (SendInput(1, ref input, InputSize) != 1)
                throw new Win32Exception("Windows blocked input to Roblox. Make sure Roblox and Finstrap run at the same privilege level.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public InputData Data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public Keyboard Key;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Keyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct LastInput { public uint Size, Time; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, ref Input input, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    }
}
