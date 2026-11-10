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
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_launchingAccounts)
                e.Cancel = true;
            else if (App.Settings.HasUnsavedChanges && Frontend.ShowMessageBox("Some account or group changes have not been saved. Close anyway?", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
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

        private async Task ConfirmAndLaunchAsync(SessionProfile[] accounts)
        {
            if (accounts.Length == 0)
                return;
            if (accounts.Length > 1 && !App.Settings.Prop.MultiInstance)
            {
                AccountStatus.Text = "Enable multi-instance launching in Settings → Integrations first.";
                return;
            }
            string preview = string.Join("\n", accounts.Select((p,i) => $"{i+1}. {p.Name} → {(string.IsNullOrWhiteSpace(AccountDestination.Text) ? p.PlaceId.ToString() : AccountDestination.Text)}"));
            if (Frontend.ShowMessageBox("Launch these accounts?\n\n" + preview, MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            await LaunchAccountsAsync(accounts);
        }

        private async Task LaunchAccountsAsync(IReadOnlyList<SessionProfile?> accounts)
        {
            if (_launchingAccounts)
                return;
            _launchingAccounts = true;
            LaunchActions.IsEnabled = false;
            try
            {
                await RefreshSessionsAsync();
                accounts = accounts.Where(p => p is null || !ActiveSessions.Any(s => s.UserId == p.UserId)).ToArray();
                if (accounts.Count == 0) { AccountStatus.Text = "These accounts already have tracked sessions."; return; }
                Queue.Begin(accounts.Where(p => p is not null).Select(p => p!.UserId));
                AccountStatus.Text = "Preparing Roblox…";
                var destination = AccountLaunchTarget.Parse(AccountDestination.Text);
                if (destination is not null)
                    destination = await destination.ResolveFriendAsync();

                var launches = new List<Func<Task<RobloxAccountLaunch?>>>(accounts.Count);
                foreach (var profile in accounts)
                {
                    var target = destination ?? new AccountLaunchTarget(PlaceId: profile?.PlaceId ?? 0);
                    launches.Add(async () => await Dispatcher.InvokeAsync(() =>
                    {
                        AccountStatus.Text = profile is null ? "Opening Roblox sign-in…"
                            : $"Launching Roblox for {profile.Name}…";
                        if (Queue.CancelRemaining) return null;
                        if (profile is not null) Queue.Set(profile.UserId, AccountLaunchState.SignInNeeded);
                        while (!Queue.CancelRemaining)
                        {
                            try
                            {
                                var result = SignIn(profile, target);
                                if (profile is not null) Queue.Set(profile.UserId, result is null ? AccountLaunchState.Cancelled : AccountLaunchState.Starting);
                                return result;
                            }
                            catch (Exception ex) when (ex is IOException or InvalidOperationException)
                            {
                                var choice = Frontend.ShowMessageBox(ex.Message + "\nYes: retry. No: skip. Cancel: stop remaining launches.", MessageBoxImage.Warning, MessageBoxButton.YesNoCancel);
                                if (choice == MessageBoxResult.Yes) continue;
                                if (profile is not null) Queue.Set(profile.UserId, AccountLaunchState.Failed);
                                if (choice == MessageBoxResult.Cancel) Queue.CancelRemaining = true;
                                return null;
                            }
                        }
                        return null;
                    }));
                }

                var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player) { AccountLaunches = launches,
                    StopAccountQueue = () => Queue.CancelRemaining,
                    AccountLaunchCompleted = (id, ready) => Dispatcher.Invoke(() => Queue.Set(id, ready ? AccountLaunchState.Ready : AccountLaunchState.Failed)) };
                await Task.Run(bootstrapper.Run);
                int started = bootstrapper.AccountPlayersStarted;
                AccountStatus.Text = started == accounts.Count
                    ? started == 1 ? "Roblox started." : $"Started {started} Roblox clients."
                    : $"Started {started} of {accounts.Count} requested Roblox clients.";
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
                _launchingAccounts = false;
                LaunchActions.IsEnabled = true;
                await RefreshSessionsAsync();
                await RestoreSelectedLayoutAsync();
            }
        }

        private RobloxAccountLaunch? SignIn(SessionProfile? profile, AccountLaunchTarget target)
        {
            var dialog = new AccountSignInDialog(profile, target, (id, name, key) => SaveVerifiedAccount(id, name, key, target)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } launch)
                return null;

            return ActiveSessions.Any(session => session.UserId == launch.UserId) ? null : launch;
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
