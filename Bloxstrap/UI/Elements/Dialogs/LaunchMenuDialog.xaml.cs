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
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_launchingAccounts)
                e.Cancel = true;
            base.OnClosing(e);
        }

        private void RefreshAccounts()
        {
            AccountsList.SelectionMode = App.Settings.Prop.MultiInstance ? SelectionMode.Multiple : SelectionMode.Single;
            var accounts = App.Settings.Prop.SessionProfiles.Where(profile => profile.UserId > 0)
                .DistinctBy(profile => profile.UserId).ToArray();
            AccountsList.ItemsSource = accounts;
            AccountHint.Text = accounts.Length == 0 ? "Add an account to launch Roblox directly from this menu."
                : App.Settings.Prop.MultiInstance ? "Check the accounts you want to launch."
                : "Select one account. Enable multi-instance launching in Integrations to select several.";
            LaunchSelected.IsEnabled = AccountsList.SelectedItems.Count > 0;
            LaunchAll.IsEnabled = App.Settings.Prop.MultiInstance && accounts.Length > 0;
        }

        private void AccountsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LaunchSelected is not null)
                LaunchSelected.IsEnabled = AccountsList.SelectedItems.Count > 0;
        }

        private async void AddAccount_Click(object sender, RoutedEventArgs e) =>
            await LaunchAccountsAsync(new SessionProfile?[] { null });

        private async void LaunchSelected_Click(object sender, RoutedEventArgs e) =>
            await ConfirmAndLaunchAsync(AccountsList.SelectedItems.Cast<SessionProfile>().ToArray());

        private async void LaunchAll_Click(object sender, RoutedEventArgs e)
        {
            if (App.Settings.Prop.MultiInstance)
                await ConfirmAndLaunchAsync(AccountsList.Items.Cast<SessionProfile>().ToArray());
        }

        private async Task ConfirmAndLaunchAsync(SessionProfile[] accounts)
        {
            if (accounts.Length == 0)
                return;
            if (accounts.Length > 1)
            {
                if (!App.Settings.Prop.MultiInstance)
                    return;
                if (Frontend.ShowMessageBox($"Launch {accounts.Length} Roblox accounts?",
                        MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
            }
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
                        return SignIn(profile, target);
                    }));
                }

                var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player) { AccountLaunches = launches };
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
                _launchingAccounts = false;
                LaunchActions.IsEnabled = true;
                RefreshAccounts();
            }
        }

        private RobloxAccountLaunch? SignIn(SessionProfile? profile, AccountLaunchTarget target)
        {
            var dialog = new AccountSignInDialog(profile, target) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } launch)
                return null;

            var profiles = App.Settings.Prop.SessionProfiles;
            var saved = profiles.FirstOrDefault(item => item.UserId == launch.UserId);
            if (saved is null)
            {
                saved = new SessionProfile { UserId = launch.UserId, Name = launch.Username };
                if (target.PlaceId > 0 && target.JobId is null && target.LinkCode is null && target.AccessCode is null)
                    saved.PlaceId = target.PlaceId;
                profiles.Add(saved);
            }
            if (saved.LaunchProfileKey != launch.ProfileKey)
            {
                saved.LaunchProfileKey = launch.ProfileKey;
            }
            if (App.Settings.HasUnsavedChanges)
            {
                App.Settings.Save();
                if (!App.Settings.LastSaveSucceeded)
                    throw new IOException("The account could not be saved. Reopen Finstrap and try again.");
            }
            return launch;
        }
    }
}
