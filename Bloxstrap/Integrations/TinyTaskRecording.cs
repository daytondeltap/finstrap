namespace Bloxstrap.Integrations
{
    internal static class TinyTaskRecording
    {
        internal readonly record struct Step(uint Message, int First, int Second, long Milliseconds)
        {
            public bool IsKeyboard => Message is 0x100 or 0x101 or 0x104 or 0x105;
            public ushort Key => (ushort)(First & 0xff);
        }

        public static List<Step> Read(string path)
        {
            if (!Path.GetExtension(path).Equals(".rec", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a TinyTask .rec recording.");

            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            // TinyTask stores 32-bit Windows EVENTMSG records, including the
            // original window handle. Playback uses the selected window instead.
            const int RecordSize = 20;
            if (file.Length == 0 || file.Length % RecordSize != 0 || file.Length > 32_000_000)
                throw new InvalidDataException("This file is not a supported TinyTask recording.");

            var steps = new List<Step>((int)(file.Length / RecordSize));
            uint previous = 0;
            long elapsed = 0;
            while (file.Position < file.Length)
            {
                uint message = reader.ReadUInt32();
                int first = reader.ReadInt32();
                int second = reader.ReadInt32();
                uint timestamp = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                if (steps.Count > 0)
                    elapsed += unchecked(timestamp - previous);

                var step = new Step(message, first, second, elapsed);
                if (elapsed > TimeSpan.FromHours(24).TotalMilliseconds ||
                    (step.IsKeyboard ? step.Key is 0 or 0x5b or 0x5c or 0x7b :
                        message is not (0x200 or 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208)))
                    throw new InvalidDataException("This recording contains unsupported input or timing. Use mouse movement, left/right/middle clicks, and standard keys. F12 stops playback.");

                steps.Add(step);
                previous = timestamp;
            }

            return steps;
        }
    }
}
