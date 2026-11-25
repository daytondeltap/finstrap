namespace Bloxstrap.Integrations
{
    internal enum AccountLaunchState { Queued, SignInNeeded, Starting, Ready, Failed, Cancelled }
    internal sealed class AccountLaunchCoordinator
    {
        private readonly Dictionary<long, AccountLaunchState> _states = new();
        public IReadOnlyDictionary<long, AccountLaunchState> States => _states;
        private volatile bool _cancelRemaining;
        public bool CancelRemaining { get => _cancelRemaining; set => _cancelRemaining = value; }
        public event Action<long, AccountLaunchState>? Changed;
        public void Set(long id, AccountLaunchState state) { _states[id] = state; Changed?.Invoke(id, state); }
        public void Begin(IEnumerable<long> ids)
        {
            CancelRemaining = false;
            foreach (long id in ids) Set(id, AccountLaunchState.Queued);
        }
        public void Finish()
        {
            foreach (long id in _states.Where(x => x.Value is AccountLaunchState.Queued or AccountLaunchState.Starting or AccountLaunchState.SignInNeeded).Select(x => x.Key).ToArray())
                Set(id, CancelRemaining ? AccountLaunchState.Cancelled : AccountLaunchState.Failed);
        }
    }
}
