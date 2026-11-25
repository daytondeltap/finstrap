using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinRT;

namespace Bloxstrap.Integrations
{
    [SupportedOSPlatform("windows10.0.19041")]
    internal sealed class WindowReplay : IDisposable
    {
        private const int ReplaySeconds = 30;
        private const int SegmentSeconds = 5;
        private readonly CancellationTokenSource _stop = new();
        private readonly Queue<(StorageFile File, TimeSpan Duration)> _segments = new();
        private readonly SemaphoreSlim _files = new(1);
        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "Finstrap-Clips", Guid.NewGuid().ToString("N"));
        private readonly Func<long> _placeId;
        private readonly string _outputFolder;
        private readonly nint _window;
        private Task? _capture;
        private int _saving;
        private const int MaximumSegments = 7;
        private volatile bool _disposed;
        private long _bufferedTicks;

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Finstrap Clips");
        public int Seconds => (int)TimeSpan.FromTicks(Interlocked.Read(ref _bufferedTicks)).TotalSeconds;
        public bool IsRunning => !_disposed && _capture is { IsCompleted: false };
        public bool IsSaving => Volatile.Read(ref _saving) != 0;
        public event Action<string>? Failed;

        public WindowReplay(nint window, Func<long> placeId, string? outputFolder = null)
        {
            _window = window;
            _placeId = placeId;
            _outputFolder = outputFolder ?? Folder;
        }

        public void Start() => _capture = Task.Run(CaptureAsync);

        private async Task CaptureAsync()
        {
            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                    throw new InvalidOperationException("Windows graphics capture is unavailable on this device.");

                var item = CreateCaptureItem(_window);
                using var device = CreateDevice();
                var size = item.Size;
                if (size.Width < 2 || size.Height < 2)
                    throw new InvalidOperationException("The Roblox window has no capturable content.");

                using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                using var session = pool.CreateCaptureSession(item);
                using var closed = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                void CloseCapture(GraphicsCaptureItem sender, object args) => closed.Cancel();
                item.Closed += CloseCapture;
                session.IsCursorCaptureEnabled = false;
                session.StartCapture();

                Directory.CreateDirectory(_scratch);
                var scratch = await StorageFolder.GetFolderFromPathAsync(_scratch);
                try
                {
                    while (!closed.IsCancellationRequested)
                    {
                        if (IsIconic(_window) || !IsWindow(_window))
                        {
                            await ClearSegmentsAsync();
                            await Task.Delay(200, closed.Token);
                            continue;
                        }
                        var currentSize = _observedSize ?? size;
                        if (currentSize.Width != size.Width || currentSize.Height != size.Height)
                        {
                            await ClearSegmentsAsync();
                            size = currentSize;
                            if (size.Width < 2 || size.Height < 2)
                                break;
                            pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                        }
                        long place = _placeId();
                        if (_bufferedPlace != place)
                            await ClearSegmentsAsync();
                        var file = await scratch.CreateFileAsync(Guid.NewGuid().ToString("N") + ".mp4");
                        TimeSpan duration = await EncodeSegmentAsync(pool, file, size.Width, size.Height, place, closed.Token);
                        if (duration > TimeSpan.Zero && _placeId() == place && !IsIconic(_window) &&
                            (_observedSize is not { } observed || (observed.Width == size.Width && observed.Height == size.Height)))
                            await AddSegmentAsync(file, duration, place);
                        else
                            File.Delete(file.Path);
                        if (duration == TimeSpan.Zero)
                            await Task.Delay(100, closed.Token);
                    }
                }
                finally { item.Closed -= CloseCapture; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteException("WindowReplay", ex);
                Failed?.Invoke("Clip capture stopped. Check that the Roblox window is open and Windows video encoding is available.");
            }
            finally
            {
                await ClearSegmentsAsync();
                try { Directory.Delete(_scratch, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

            }
        }

        private long _bufferedPlace;
        private Windows.Graphics.SizeInt32? _observedSize;

        private async Task<TimeSpan> EncodeSegmentAsync(Direct3D11CaptureFramePool pool, StorageFile file,
            int width, int height, long place, CancellationToken token)
        {
            var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            input.FrameRate.Numerator = 30;
            input.FrameRate.Denominator = 1;
            var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
            source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            double scale = Math.Min(1, Math.Min(1280.0 / width, 720.0 / height));
            profile.Video.Width = (uint)Math.Max(2, (int)(width * scale) & ~1);
            profile.Video.Height = (uint)Math.Max(2, (int)(height * scale) & ~1);
            profile.Video.FrameRate.Numerator = 30;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Denominator = 1;
            profile.Video.Bitrate = 3_500_000;
            profile.Audio = null;
            TimeSpan? origin = null;
            TimeSpan end = TimeSpan.Zero;
            var outstanding = new System.Collections.Concurrent.ConcurrentDictionary<Direct3D11CaptureFrame, byte>();
            var segmentClock = Stopwatch.StartNew();

            source.SampleRequested += async (sender, args) =>
            {
                var deferral = args.Request.GetDeferral();
                try
                {
                    while (!token.IsCancellationRequested && !IsIconic(_window) && IsWindow(_window) &&
                           _placeId() == place && segmentClock.Elapsed < TimeSpan.FromSeconds(SegmentSeconds + 2))
                    {
                        var frame = pool.TryGetNextFrame();
                        if (frame is null)
                        {
                            await Task.Delay(8, token);
                            continue;
                        }
                        var timestamp = frame.SystemRelativeTime;
                        if (frame.ContentSize.Width != width || frame.ContentSize.Height != height)
                        {
                            _observedSize = frame.ContentSize;
                            frame.Dispose();
                            break;
                        }
                        origin ??= timestamp;
                        var offset = timestamp - origin.Value;
                        if (offset >= TimeSpan.FromSeconds(SegmentSeconds))
                        {
                            frame.Dispose();
                            break;
                        }
                        if (end > offset)
                        {
                            frame.Dispose();
                            continue;
                        }
                        outstanding.TryAdd(frame, 0);
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, offset);
                        sample.Duration = TimeSpan.FromSeconds(1.0 / 30);
                        sample.Processed += (_, _) =>
                        {
                            if (outstanding.TryRemove(frame, out _))
                                frame.Dispose();
                        };
                        end = offset + sample.Duration;
                        args.Request.Sample = sample;
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    App.Logger.WriteException("WindowReplay::Sample", ex);
                    source.NotifyError(MediaStreamSourceErrorStatus.Other);
                }
                finally { deferral.Complete(); }
            };

            try
            {
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                var prepared = await encoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
                if (!prepared.CanTranscode)
                    throw new InvalidOperationException($"Windows could not encode H.264 video ({prepared.FailureReason}).");
                try { await prepared.TranscodeAsync().AsTask(token); }
                catch (COMException ex) when (ex.HResult == unchecked((int)0xC00D4A44) && end == TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }
                return end;
            }
            finally
            {
                foreach (var frame in outstanding.Keys)
                    if (outstanding.TryRemove(frame, out _))
                        frame.Dispose();
            }
        }

        private async Task AddSegmentAsync(StorageFile file, TimeSpan duration, long place)
        {
            await _files.WaitAsync();
            try
            {
                _bufferedPlace = place;
                _segments.Enqueue((file, duration));
                long ticks = _segments.Sum(segment => segment.Duration.Ticks);
                while (_segments.Count > MaximumSegments || (_segments.Count > 1 && ticks - _segments.Peek().Duration.Ticks >= TimeSpan.FromSeconds(ReplaySeconds).Ticks))
                {
                    var removed = _segments.Dequeue();
                    ticks -= removed.Duration.Ticks;
                    File.Delete(removed.File.Path);
                }
                Interlocked.Exchange(ref _bufferedTicks, ticks);
            }
            finally { _files.Release(); }
        }

        private async Task ClearSegmentsAsync()
        {
            await _files.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _bufferedTicks, 0);
                while (_segments.TryDequeue(out var segment))
                    File.Delete(segment.File.Path);
            }
            finally { _files.Release(); }
        }

        public async Task<string> SaveAsync()
        {
            if (Interlocked.Exchange(ref _saving, 1) != 0)
                throw new InvalidOperationException("A clip is already being saved.");
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(_outputFolder);
                string snapshot = Path.Combine(_outputFolder, "." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(snapshot);
                temporary = snapshot;
                var composition = new MediaComposition();
                long place;
                await _files.WaitAsync();
                try
                {
                    place = _bufferedPlace;
                    if (!IsRunning || _segments.Count == 0 || place != _placeId() || IsIconic(_window))
                        throw new InvalidOperationException("The selected Roblox window has no clip ready yet.");
                    foreach (var segment in _segments)
                    {
                        string path = Path.Combine(snapshot, Path.GetFileName(segment.File.Path));
                        File.Copy(segment.File.Path, path);
                        composition.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path)));
                    }
                }
                finally { _files.Release(); }

                var excess = composition.Duration - TimeSpan.FromSeconds(ReplaySeconds);
                while (excess > TimeSpan.Zero && composition.Clips.Count > 0)
                {
                    var first = composition.Clips[0];
                    if (excess < first.OriginalDuration)
                    {
                        first.TrimTimeFromStart = excess;
                        break;
                    }
                    excess -= first.OriginalDuration;
                    composition.Clips.RemoveAt(0);
                }
                var outputProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
                outputProfile.Audio = null;
                var staging = await StorageFolder.GetFolderFromPathAsync(snapshot);
                var output = await staging.CreateFileAsync("Clip.mp4");
                var reason = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, outputProfile);
                if (reason != TranscodeFailureReason.None || new FileInfo(output.Path).Length == 0)
                    throw new IOException($"Windows could not save this clip ({reason}).");
                string label = place > 0 ? $"Place {place}" : "Roblox";
                string final = Path.Combine(_outputFolder, $"{label} - {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}.mp4");
                File.Move(output.Path, final);
                return final;
            }
            finally
            {
                Interlocked.Exchange(ref _saving, 0);
                if (temporary is not null)
                {
                    try { Directory.Delete(temporary, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public async Task StopAsync()
        {
            Dispose();
            if (_capture is not null)
                await _capture;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stop.Cancel();
        }

        private static unsafe GraphicsCaptureItem CreateCaptureItem(nint window)
        {
            using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            Guid interop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interop, out nint pointer));
            try
            {
                var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)pointer)[3];
                nint item = 0;
                Marshal.ThrowExceptionForHR(create(pointer, window, &itemId, &item));
                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(pointer); }
        }

        private static IDirect3DDevice CreateDevice()
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out nint device, 0, 0));
            try
            {
                Guid dxgi = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in dxgi, out nint dxgiDevice));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint graphicsDevice));
                    try { return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice); }
                    finally { Marshal.Release(graphicsDevice); }
                }
                finally { Marshal.Release(dxgiDevice); }
            }
            finally { Marshal.Release(device); }
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
            nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, nint featureLevel, nint context);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(nint window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(nint window);
    }
}
