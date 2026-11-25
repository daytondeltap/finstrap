using System.IO.Pipes;

namespace Bloxstrap.Integrations
{
    internal sealed record SessionSnapshot(int ProcessId, long StartedUtc, long UserId, bool InGame, long PlaceId);

    internal static class SessionChannel
    {
        public static string Name(int pid) => $"Finstrap-Session-{pid}";
        // Each watcher serves its own current-user-only endpoint; no tokens or launch arguments cross it.
        public static async Task ServeAsync(int pid, Func<SessionSnapshot> snapshot, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name(pid), PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token);
                    using var writer = new StreamWriter(pipe) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot()).AsMemory(), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (IOException) { await Task.Delay(250, token); }
            }
        }

        public static async Task<SessionSnapshot?> ReadAsync(int pid, CancellationToken token)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(500);
                using var pipe = new NamedPipeClientStream(".", Name(pid), PipeDirection.In,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(pipe);
                string? line = await reader.ReadLineAsync(timeout.Token);
                var state = line is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(line);
                using var process = Process.GetProcessById(pid);
                return state is not null && state.ProcessId == pid && process.ProcessName == App.RobloxPlayerAppName &&
                    process.StartTime.ToUniversalTime().Ticks == state.StartedUtc && !process.HasExited ? state : null;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException or
                InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { return null; }
        }
    }
}
