using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using Bloxstrap.Integrations;
using Bloxstrap.UI.ViewModels.Installer;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class LaunchMenuDialog
    {
        public NextAction CloseAction = NextAction.Terminate;
        private bool _launchingAccounts;
        private AccountLaunchTarget? _retryRejoinTarget;
        private LaunchGroup? _retryGroup;
        private AccountLaunchTarget? _retryDestination;

        public LaunchMenuDialog()
        {
            var viewModel = new LaunchMenuViewModel();
            viewModel.CloseWindowRequest += (_, closeAction) =>
            {
                CloseAction = closeAction;
                Close();
            };

            DataContext = viewModel;
            InitializeComponent();
            RefreshAccounts();
            InitializeDashboard();
            InitializeServerCountries();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_launchingAccounts)
                e.Cancel = true;
            else if (!SmokeMode && App.Settings.HasUnsavedChanges && Frontend.ShowMessageBox("Some account or group changes have not been saved. Close anyway?", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                e.Cancel = true;
            base.OnClosing(e);
        }

        private void RefreshAccounts()
        {
            AccountsList.SelectionMode = App.Settings.Prop.MultiInstance ? SelectionMode.Multiple : SelectionMode.Single;
            var accounts = App.Settings.Prop.SessionProfiles.Where(p => p.UserId > 0).DistinctBy(p => p.UserId)
                .Select(p => new AccountCard(p)).ToArray();
            AccountsList.ItemsSource = accounts;
            AccountHint.Text = accounts.Length == 0 ? "Add an account to get started." : "Select accounts to launch. Session status comes from verified Finstrap watchers.";
            LaunchSelected.IsEnabled = AccountsList.SelectedItems.Count > 0;
            LaunchAll.IsEnabled = App.Settings.Prop.MultiInstance && accounts.Length > 0;
        }

        private void AccountsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LaunchSelected is not null)
                LaunchSelected.IsEnabled = AccountsList.SelectedItems.Count > 0;
        }

        private void AddAccount_Click(object sender, RoutedEventArgs e) => LinkAccount(null);

        private async void AddAndLaunch_Click(object sender, RoutedEventArgs e)
        {
            var players = Process.GetProcessesByName(App.RobloxPlayerAppName);
            foreach (var player in players)
                player.Dispose();

            if (!App.Settings.Prop.MultiInstance && players.Length > 0)
            {
                if (LinkAccount(null))
                    AccountStatus.Text = "Enable multi-instance launching in Settings → Integrations to launch another Roblox account.";
                return;
            }

            await LaunchAccountsAsync(new SessionProfile?[] { null });
        }

        private async void LaunchSelected_Click(object sender, RoutedEventArgs e) =>
            await ConfirmAndLaunchAsync(Cards.Where(c => AccountsList.SelectedItems.Contains(c)).Select(c => c.Profile).ToArray());

        private async void LaunchAll_Click(object sender, RoutedEventArgs e)
        {
            if (App.Settings.Prop.MultiInstance)
                await ConfirmAndLaunchAsync(AccountsList.Items.Cast<AccountCard>().Select(c => c.Profile).ToArray());
        }

        private async Task ConfirmAndLaunchAsync(SessionProfile[] accounts, LaunchGroup? group = null)
        {
            if (accounts.Length == 0)
                return;
            if (accounts.Length > 1 && !App.Settings.Prop.MultiInstance)
            {
                AccountStatus.Text = "Enable multi-instance launching in Settings → Integrations first.";
                return;
            }
            string? country = SelectedServerCountry;
            string destinationText = group?.Destination ?? AccountDestination.Text;
            try
            {
                var destination = AccountLaunchTarget.Parse(destinationText);
                if (accounts.Length > 1 && (country is null ||
                    accounts.All(account => !(destination ?? new AccountLaunchTarget(PlaceId: account.PlaceId)).IsHome)))
                {
                    string preview = string.Join("\n", accounts.Select(p =>
                        $"{p.Name} → {(string.IsNullOrWhiteSpace(destinationText) ? p.PlaceId.ToString() : destinationText)}"));
                    if (group?.PlayerChannel is { } channel)
                        preview += $"\n\nRoblox channel: {channel}";
                    if (country is not null)
                        preview += $"\n\nServer country: {(ServerCountry.SelectedItem as CountryChoice)?.Name ?? country}";
                    if (Frontend.ShowMessageBox("Launch these accounts?\n\n" + preview,
                        MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;
                }

                await LaunchAccountsAsync(accounts, destination, group: group);
            }
            catch (FormatException ex)
            {
                AccountStatus.Text = ex.Message;
            }
        }

        private async Task LaunchAccountsAsync(IReadOnlyList<SessionProfile?> accounts,
            AccountLaunchTarget? selectedDestination = null, bool exactRejoin = false,
            LaunchGroup? group = null)
        {
            if (_launchingAccounts)
                return;
            _launchingAccounts = true;
            LaunchActions.IsEnabled = false;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(DashboardClosed.Token);
            _serverOperation = operation;
            var locations = new List<ServerLocation>();
            var failures = new List<string>();
            try
            {
                string? country = exactRejoin ? null : SelectedServerCountry;
                var destination = selectedDestination ?? AccountLaunchTarget.Parse(group?.Destination ?? AccountDestination.Text);
                if (country is not null && accounts.Any(profile =>
                    (destination ?? new AccountLaunchTarget(PlaceId: profile?.PlaceId ?? 0)).IsHome))
                {
                    ShowGameFinder(new(accounts, group));
                    AccountStatus.Text = "Choose a game here in Finstrap to apply your server country.";
                    return;
                }

                _pendingGameLaunch = null;
                await RefreshSessionsAsync();
                accounts = accounts.Where(p => p is null || !ActiveSessions.Any(s => s.UserId == p.UserId)).ToArray();
                if (accounts.Count == 0)
                {
                    AccountStatus.Text = exactRejoin ? "Close this account's current Roblox window before rejoining." :
                        "These accounts already have tracked sessions.";
                    return;
                }
                Queue.Begin(accounts.Where(p => p is not null).Select(p => p!.UserId));
                _retryRejoinTarget = exactRejoin ? destination : null;
                _retryGroup = group;
                _retryDestination = selectedDestination;
                AccountStatus.Text = "Preparing Roblox…";
                if (destination is not null && country is null)
                    destination = await destination.ResolveFriendAsync();

                var launches = new List<Func<Task<RobloxAccountLaunch?>>>(accounts.Count);
                foreach (var profile in accounts)
                {
                    var target = destination ?? new AccountLaunchTarget(PlaceId: profile?.PlaceId ?? 0);
                    launches.Add(async () => await await Dispatcher.InvokeAsync(async () =>
                    {
                        if (Queue.CancelRemaining)
                            return null;
                        try
                        {
                            return await PrepareLaunchAsync(profile, target, country, locations, operation.Token);
                        }
                        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return null; }
                        catch (Exception ex)
                        {
                            failures.Add($"{profile?.Name ?? "Roblox"}: {DescribeLaunchFailure(ex)}");
                            if (profile is not null)
                                Queue.Set(profile.UserId, AccountLaunchState.Failed);
                            return null;
                        }
                    }));
                }

                var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player) { AccountLaunches = launches,
                    PlayerChannelOverride = group?.PlayerChannel,
                    StopAccountQueue = () => Queue.CancelRemaining,
                    AccountLaunchCompleted = (id, ready) => Dispatcher.Invoke(() => Queue.Set(id, ready ? AccountLaunchState.Ready : AccountLaunchState.Failed)) };
                await Task.Run(bootstrapper.Run);
                int started = bootstrapper.AccountPlayersStarted;
                AccountStatus.Text = bootstrapper.PlayerSelectionError ?? (started == accounts.Count
                    ? exactRejoin ? "Roblox is opening the recorded server. It must still be active and accessible."
                        : started == 1 ? "Roblox started." : $"Started {started} Roblox clients."
                    : $"Started {started} of {accounts.Count} requested Roblox clients.");
                if (operation.IsCancellationRequested)
                    AccountStatus.Text += " Remaining launches cancelled.";
                if (locations.Count > 0)
                    AccountStatus.Text += "\nSelected server locations (estimated): " + string.Join("; ", locations.Distinct().Select(location => location.ToString()).Distinct());
                if (failures.Count > 0)
                    AccountStatus.Text += "\n" + string.Join("\n", failures.Distinct());
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                AccountStatus.Text = ex.Message;
            }
            catch (Exception)
            {
                App.Logger.WriteLine("LaunchMenu::AccountLaunch", "Roblox could not complete the launch.");
                AccountStatus.Text = "Roblox could not complete the launch. Check your connection and try again.";
            }
            finally
            {
                Queue.Finish();
                _serverOperation = null;
                _launchingAccounts = false;
                LaunchActions.IsEnabled = true;
                await RefreshSessionsAsync();
                await RestoreSelectedLayoutAsync();
            }
        }

        private async Task<RobloxAccountLaunch?> PrepareLaunchAsync(SessionProfile? profile, AccountLaunchTarget target,
            string? country, List<ServerLocation> locations, CancellationToken token)
        {
            if (profile is null)
            {
                AccountStatus.Text = "Sign in to link your Roblox account.";
                profile = PromptToLinkAccount(null, target);
                if (profile is null)
                    return null;
            }
            if (ActiveSessions.Any(session => session.UserId == profile.UserId))
                throw new FormatException("This account already has a tracked Roblox session.");

            AccountStatus.Text = $"Verifying {profile.Name}…";
            using var client = await OpenLinkedClientAsync(profile, token);
            var session = new RobloxAccountSession(client);
            var identity = await VerifyLinkedAccountAsync(session, profile, token);
            ServerLocation? location = null;
            if (country is not null)
            {
                string region = (ServerCountry.SelectedItem as CountryChoice)?.Name ?? country;
                AccountStatus.Text = $"Finding a public server in {region} for {profile.Name}…";
                location = await new ServerRegionLookup(App.HttpClient, session, Datacenters)
                    .FindAsync(target, country, token, progress =>
                    {
                        if (!token.IsCancellationRequested)
                            AccountStatus.Text = $"Finding a public server in {region} for {profile.Name}… " +
                                $"{progress.Checked} checked, {progress.Located} located.";
                    });
                target = target with { JobId = location.JobId.ToString("D") };
                AccountStatus.Text = $"Found an estimated match in {location}. Opening Roblox for {profile.Name}…";
            }
            else
            {
                target = await session.ResolveShareAsync(target, token);
            }

            string ticket = await session.GetTicketAsync(token);
            if (location is not null)
                locations.Add(location);
            Queue.Set(profile.UserId, AccountLaunchState.Starting);
            return new RobloxAccountLaunch(identity.Id, identity.Name, profile.LaunchProfileKey!, ticket, target, location);
        }

        private void SaveVerifiedAccount(long userId, string username, string profileKey, AccountLaunchTarget target)
        {
            var profiles = App.Settings.Prop.SessionProfiles;
            var saved = profiles.FirstOrDefault(item => item.UserId == userId);
            bool isNew = saved is null;
            if (saved is null)
            {
                saved = new SessionProfile { UserId = userId, Name = username };
                if (target.PlaceId > 0 && target.JobId is null && target.LinkCode is null && target.AccessCode is null)
                    saved.PlaceId = target.PlaceId;
                profiles.Add(saved);
            }
            string previousUsername = saved.Username;
            string? previousKey = saved.LaunchProfileKey;
            saved.Username = username;
            saved.LaunchProfileKey = profileKey;
            if (App.Settings.HasUnsavedChanges)
            {
                App.Settings.Save();
                if (!App.Settings.LastSaveSucceeded)
                {
                    if (isNew)
                        profiles.Remove(saved);
                    else
                    {
                        saved.Username = previousUsername;
                        saved.LaunchProfileKey = previousKey;
                    }
                    throw new IOException("The account could not be saved. Reopen Finstrap and try again.");
                }
                RefreshAccounts();
                if (!SmokeMode)
                    _ = LoadAvatarsAsync();
            }
        }
    }
}
