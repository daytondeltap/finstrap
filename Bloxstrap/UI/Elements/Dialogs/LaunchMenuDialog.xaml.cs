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
        }

        private void AccountsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LaunchSelected is not null)
                LaunchSelected.IsEnabled = AccountsList.SelectedItems.Count > 0;
        }

        private async void AddAccount_Click(object sender, RoutedEventArgs e) =>
            await LaunchAccountsAsync(new SessionProfile?[] { null });

        private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = AccountsList.SelectedItems.Cast<SessionProfile>().ToArray();
            if (selected.Length == 0)
                return;
            if (selected.Length > 1)
            {
                if (!App.Settings.Prop.MultiInstance)
                    return;
                string accounts = string.Join("\n", selected.Select(profile => profile.Name));
                if (Frontend.ShowMessageBox($"Launch Roblox for these accounts?\n\n{accounts}",
                        MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
            }
            await LaunchAccountsAsync(selected);
        }

        private async Task LaunchAccountsAsync(IReadOnlyList<SessionProfile?> accounts)
        {
            if (_launchingAccounts)
                return;
            _launchingAccounts = true;
            LaunchActions.IsEnabled = false;
            int started = 0;
            try
            {
                foreach (var profile in accounts)
                {
                    AccountStatus.Text = profile is null ? "Preparing Roblox sign-in…"
                        : $"Preparing Roblox for {profile.Name}…";
                    var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player)
                    {
                        AccountLaunch = async () => await Dispatcher.InvokeAsync(() => SignIn(profile))
                    };
                    try
                    {
                        await Task.Run(bootstrapper.Run);
                        if (bootstrapper.AccountPlayerStarted)
                            started++;
                    }
                    catch (Exception)
                    {
                        App.Logger.WriteLine("LaunchMenu::AccountLaunch", "Roblox could not start an account session.");
                    }
                }
                AccountStatus.Text = started == accounts.Count
                    ? started == 1 ? "Roblox started." : $"Started {started} Roblox clients."
                    : $"Started {started} of {accounts.Count} requested Roblox clients.";
            }
            finally
            {
                _launchingAccounts = false;
                LaunchActions.IsEnabled = true;
                RefreshAccounts();
            }
        }

        private RobloxAccountLaunch? SignIn(SessionProfile? profile)
        {
            var dialog = new AccountSignInDialog(profile) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } launch)
                return null;

            var profiles = App.Settings.Prop.SessionProfiles;
            var saved = profiles.FirstOrDefault(item => item.UserId == launch.UserId);
            if (saved is null)
            {
                saved = new SessionProfile { UserId = launch.UserId, Name = launch.Username };
                profiles.Add(saved);
            }
            if (saved.LaunchProfileKey != launch.ProfileKey)
            {
                saved.LaunchProfileKey = launch.ProfileKey;
                App.Settings.Save();
            }
            return launch;
        }
    }
}
