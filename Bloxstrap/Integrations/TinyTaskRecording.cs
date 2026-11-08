using System.Buffers.Binary;

namespace Bloxstrap.Integrations
{
    internal static class TinyTaskRecording
    {
        internal readonly record struct Step(uint Message, int First, int Second, long Milliseconds)
        {
            public bool IsKeyboard => Message is 0x100 or 0x101 or 0x104 or 0x105;
            public ushort Key => (ushort)(First & 0xff);
        }

        public static List<Step> Read(string path, CancellationToken token = default)
        {
            if (!Path.GetExtension(path).Equals(".rec", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a TinyTask .rec recording.");

            using var file = File.OpenRead(path);
            // TinyTask stores 32-bit Windows EVENTMSG records, including the
            // original window handle. Playback uses the selected window instead.
            const int RecordSize = 20;
            long length = file.Length;
            if (length == 0 || length % RecordSize != 0 || length > 32_000_000)
                throw new InvalidDataException("This file is not a supported TinyTask recording.");

            int count = (int)(length / RecordSize);
            var steps = new List<Step>(count);
            uint previous = 0;
            long elapsed = 0;
            var buffer = new byte[Math.Min(count, 2048) * RecordSize];
            for (int index = 0; index < count;)
            {
                token.ThrowIfCancellationRequested();
                int bytesToRead = Math.Min((count - index) * RecordSize, buffer.Length);
                int bytesRead = 0;
                while (bytesRead < bytesToRead)
                {
                    int read = file.Read(buffer, bytesRead, bytesToRead - bytesRead);
                    if (read == 0)
                        throw new EndOfStreamException("The TinyTask recording ended before all records could be read.");
                    bytesRead += read;
                }

                for (int offset = 0; offset < bytesToRead; offset += RecordSize, index++)
                {
                    ReadOnlySpan<byte> record = buffer.AsSpan(offset, RecordSize);
                    uint message = BinaryPrimitives.ReadUInt32LittleEndian(record);
                    int first = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
                    int second = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
                    uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
                    if (index > 0)
                        elapsed += unchecked(timestamp - previous);

                    var step = new Step(message, first, second, elapsed);
                    if (elapsed > 24L * 60 * 60 * 1000 ||
                        (step.IsKeyboard ? step.Key is 0 or 0x5b or 0x5c or 0x7b :
                            message is not (0x200 or 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208)))
                        throw new InvalidDataException("This recording contains unsupported input or timing. Use mouse movement, left/right/middle clicks, and standard keys. F12 stops playback.");

                    steps.Add(step);
                    previous = timestamp;
                }
            }

            return steps;
        }
    }
}
