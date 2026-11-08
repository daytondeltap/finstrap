namespace Bloxstrap.Models
{
    internal class WatcherData
    {
        public int ProcessId { get; set; }
        public bool MultiInstance { get; set; }
        public long ProcessStartTimeUtc { get; set; }
        public long AccountUserId { get; set; }
        public bool KeepDesktopApp { get; set; }

        public string? LogFile { get; set; }

        public List<int>? AutoclosePids { get; set; }
    }
}
