namespace Bloxstrap.Integrations
{
    // Hold names only. Never open or modify another Roblox process's handles.
    internal sealed class MultiInstanceLease : IDisposable
    {
        private readonly Mutex _singleton;
        private readonly Mutex _legacySingleton;

        public MultiInstanceLease()
        {
            _legacySingleton = new Mutex(false, "ROBLOX_singletonMutex");
            try
            {
                _singleton = new Mutex(false, "ROBLOX_singletonEvent");
            }
            catch
            {
                _legacySingleton.Dispose();
                throw;
            }
        }

        public static EventWaitHandle CreateReadyEvent(int pid) =>
            new(false, EventResetMode.ManualReset, $"{App.ProjectName}-InstanceReady-{pid}");

        public void Dispose()
        {
            _singleton.Dispose();
            _legacySingleton.Dispose();
        }
    }
}
