using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media.Editing;
using Windows.Storage;

namespace Bloxstrap.Integrations
{
    [SupportedOSPlatform("windows10.0.19041")]
    internal static class WindowReplaySmoke
    {
        public static async Task RunAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var label = new System.Windows.Controls.TextBlock { FontSize = 32, Margin = new Thickness(22) };
            var window = new Window { Title = "Finstrap clip test", Content = label, Width = 640, Height = 360, ShowInTaskbar = false };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(34) };
            timer.Tick += (_, _) => label.Text = $"Finstrap clip test {Environment.TickCount64}";
            int exitCode = 1;
            try
            {
                window.Show();
                timer.Start();
                using var replay = new WindowReplay(new WindowInteropHelper(window).Handle, () => 123, directory);
                try
                {
                    string? failure = null;
                    replay.Failed += message => Volatile.Write(ref failure, message);
                    replay.Start();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                    while (replay.Seconds == 0)
                    {
                        if (Volatile.Read(ref failure) is { } message)
                            throw new InvalidOperationException(message);
                        await Task.Delay(100, timeout.Token);
                    }
                    string file = await Task.Run(replay.SaveAsync);
                    var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(file));
                    if (clip.OriginalDuration.TotalSeconds is < 1 or > 30 || !Path.GetFileName(file).StartsWith("Place 123"))
                        throw new InvalidDataException("Unexpected replay duration or place label.");
                    File.WriteAllText(Path.Combine(directory, "result.txt"), $"PASS: captured and decoded a {clip.OriginalDuration.TotalSeconds:F2}s MP4.");
                    exitCode = 0;
                }
                finally { await replay.StopAsync(); }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "result.txt"), ex + Environment.NewLine + App.Logger.AsDocument);
            }
            finally
            {
                timer.Stop();
                window.Close();
                Application.Current.Shutdown(exitCode);
            }
        }
    }
}
