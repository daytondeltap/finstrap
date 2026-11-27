using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class LaunchMenuDialog
    {
        private sealed record CountryChoice(string Code, string Name);
        private bool _countryReady;
        private string? SelectedServerCountry => (ServerCountry.SelectedItem as CountryChoice)?.Code is { Length: > 0 } code ? code : null;

        private void InitializeServerCountries()
        {
            var countries = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Select(c => new RegionInfo(c.Name))
                .Where(r => Regex.IsMatch(r.TwoLetterISORegionName, @"\A[A-Z]{2}\z"))
                .DistinctBy(r => r.TwoLetterISORegionName)
                .OrderBy(r => r.EnglishName)
                .Select(r => new CountryChoice(r.TwoLetterISORegionName, r.EnglishName))
                .ToList();
            countries.Insert(0, new("", "Any region"));
            if (App.Settings.Prop.PreferredServerCountry is { Length: > 0 } saved && countries.All(c => c.Code != saved))
                countries.Add(new(saved, saved));
            ServerCountry.ItemsSource = countries;
            ServerCountry.SelectedValue = App.Settings.Prop.PreferredServerCountry ?? "";
            _countryReady = true;
        }

        private void ServerCountry_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_countryReady)
                return;

            string? previous = App.Settings.Prop.PreferredServerCountry;
            App.Settings.Prop.PreferredServerCountry = SelectedServerCountry;
            App.Settings.Save();
            UpdateGameFinderHint();
            if (!App.Settings.LastSaveSucceeded)
            {
                App.Settings.Prop.PreferredServerCountry = previous;
                _countryReady = false;
                ServerCountry.SelectedValue = previous ?? "";
                _countryReady = true;
                UpdateGameFinderHint();
                AccountStatus.Text = "Could not save the server country. Reopen Finstrap and try again.";
            }
        }

        private static readonly RobloxDatacenters Datacenters = new(App.HttpClient);
        private sealed record GameChoice(string Name, AccountLaunchTarget Target);
        private sealed record PendingGameLaunch(IReadOnlyList<SessionProfile?> Accounts, LaunchGroup? Group);
        private PendingGameLaunch? _pendingGameLaunch;
        private CancellationTokenSource? _serverOperation;
        private bool _searchingGames;

        private void ShowGameFinder(PendingGameLaunch? pending = null)
        {
            _pendingGameLaunch = pending;
            GameFinder.Visibility = Visibility.Visible;
            UpdateGameFinderHint();
            GameQuery.Focus();
        }

        private void UpdateGameFinderHint()
        {
            UseGame.Content = _pendingGameLaunch is null ? "Use selected game" : "Play selected game";
            string region = (ServerCountry.SelectedItem as CountryChoice)?.Name ?? "Any region";
            GameFinderHint.Text = _pendingGameLaunch is null
                ? $"Choose a game, then play with the accounts you select. Server country: {region}."
                : $"Choose a game for {_pendingGameLaunch.Accounts.Count} queued account(s). Server country: {region}.";
        }

        private void FindGame_Click(object sender, RoutedEventArgs e)
        {
            if (!_launchingAccounts)
                ShowGameFinder(_pendingGameLaunch);
        }

        private void CloseGameFinder_Click(object sender, RoutedEventArgs e)
        {
            _pendingGameLaunch = null;
            GameFinder.Visibility = Visibility.Collapsed;
        }

        private async void SearchGames_Click(object sender, RoutedEventArgs e) => await SearchGamesAsync();

        private async void GameQuery_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SearchGamesAsync();
            }
        }

        private async Task SearchGamesAsync()
        {
            string query = GameQuery.Text.Trim();
            if (_launchingAccounts || _searchingGames || query.Length == 0)
                return;

            _searchingGames = true;
            SearchGamesButton.IsEnabled = false;
            GameResults.ItemsSource = null;
            try
            {
                if (query.All(char.IsAsciiDigit) || query.Contains("://", StringComparison.Ordinal))
                {
                    var target = AccountLaunchTarget.Parse(query);
                    if (target is not { PlaceId: > 0, AccessCode: null, LinkCode: null, ShareCode: null } || target.UserId != 0)
                        throw new FormatException("Enter a public place ID or public Roblox link.");
                    GameResults.ItemsSource = new[] { new GameChoice(
                        target.JobId is null ? $"Place {target.PlaceId}" : $"Specific server in place {target.PlaceId}", target) };
                    GameResults.SelectedIndex = 0;
                    return;
                }

                if (query.Length > 100)
                    throw new FormatException("Enter a game name of up to 100 characters.");
                AccountStatus.Text = "Searching Roblox games…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(DashboardClosed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var results = await new RobloxGameSearch(App.HttpClient).SearchAsync(query, timeout.Token);
                GameResults.ItemsSource = results.Select(game => new GameChoice(game.Name, new(PlaceId: game.PlaceId))).ToArray();
                GameResults.SelectedIndex = results.Count > 0 ? 0 : -1;
                AccountStatus.Text = results.Count > 0 ? "Choose a game from the results." :
                    "No public games found. Try another name or paste a place ID.";
            }
            catch (FormatException ex) { AccountStatus.Text = ex.Message; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                if (!DashboardClosed.IsCancellationRequested)
                    AccountStatus.Text = "Roblox game search is unavailable. You can still enter a place ID or public game link.";
            }
            finally
            {
                _searchingGames = false;
                SearchGamesButton.IsEnabled = true;
            }
        }

        private void GameResults_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UseGame.IsEnabled = GameResults.SelectedItem is GameChoice;

        private async void GameResults_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(GameResults, source) is ListBoxItem)
                await UseSelectedGameAsync();
        }

        private async void UseGame_Click(object sender, RoutedEventArgs e) => await UseSelectedGameAsync();

        private async Task UseSelectedGameAsync()
        {
            if (_launchingAccounts || GameResults.SelectedItem is not GameChoice game)
                return;

            var pending = _pendingGameLaunch;
            SetDestination(game.Target);
            _pendingGameLaunch = null;
            GameFinder.Visibility = Visibility.Collapsed;
            if (pending is null)
                AccountStatus.Text = $"Selected {game.Name}. Choose the accounts to play with.";
            else
            {
                var accounts = pending.Accounts.Where(profile => profile is null ||
                    App.Settings.Prop.SessionProfiles.Any(linked => linked.UserId == profile.UserId)).ToArray();
                if (accounts.Length == 0)
                    AccountStatus.Text = "Select a linked account to play this game.";
                else
                    await LaunchAccountsAsync(accounts, game.Target, group: pending.Group);
            }
        }

        private void SetDestination(AccountLaunchTarget target)
        {
            string place = target.PlaceId.ToString(CultureInfo.InvariantCulture);
            AccountDestination.Text = target.JobId is null ? place :
                $"https://www.roblox.com/games/{place}/?gameInstanceId={target.JobId}";
        }

        internal void SetBrowserDestination(AccountLaunchTarget target)
        {
            SetDestination(target);
            if (AccountsList.Items.Count == 1)
                AccountsList.SelectedIndex = 0;
            AccountStatus.Text = "Use Play with selected accounts to join in your selected country.";
        }

        private async Task<HttpClient> OpenLinkedClientAsync(SessionProfile profile, CancellationToken token)
        {
            string message = $"Use Reconnect on {profile.Name} to restore its Roblox sign-in.";
            if (profile.LaunchProfileKey is not string key || !Guid.TryParseExact(key, "N", out _))
                throw new FormatException(message);

            using var view = new WebView2 { IsHitTestVisible = false, Focusable = false };
            AccountSessionHost.Children.Add(view);
            try
            {
                await RobloxWebProfile.InitializeAsync(view, key, token);
                return await RobloxWebProfile.OpenClientAsync(view, token) ?? throw new FormatException(message);
            }
            finally
            {
                AccountSessionHost.Children.Remove(view);
            }
        }

        private static async Task<(long Id, string Name)> VerifyLinkedAccountAsync(
            RobloxAccountSession session, SessionProfile profile, CancellationToken token)
        {
            var identity = await session.GetIdentityAsync(token);
            if (identity is null || identity.Value.Id != profile.UserId)
                throw new FormatException($"Use Reconnect on {profile.Name} to restore its Roblox sign-in.");
            return identity.Value;
        }

        private async void CheckServerLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_launchingAccounts)
                return;

            var profile = (AccountsList.SelectedItem as AccountCard)?.Profile;
            if (profile is null)
            {
                AccountStatus.Text = "Select a linked account to check public server locations.";
                return;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(DashboardClosed.Token);
            _serverOperation = operation;
            _launchingAccounts = true;
            LaunchActions.IsEnabled = false;
            try
            {
                var target = AccountLaunchTarget.Parse(AccountDestination.Text) ?? new(PlaceId: profile.PlaceId);
                if (target.IsHome)
                {
                    ShowGameFinder();
                    AccountStatus.Text = "Choose a game before checking its public servers.";
                    return;
                }

                AccountStatus.Text = "Checking public server locations…";
                using var client = await OpenLinkedClientAsync(profile, operation.Token);
                var session = new RobloxAccountSession(client);
                await VerifyLinkedAccountAsync(session, profile, operation.Token);
                var location = await new ServerRegionLookup(App.HttpClient, session, Datacenters)
                    .FindAsync(target, target.JobId is null ? SelectedServerCountry : null, operation.Token);
                AccountStatus.Text = $"Estimated server location: {location}";
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { AccountStatus.Text = "Server check cancelled."; }
            catch (Exception ex) { AccountStatus.Text = DescribeLaunchFailure(ex); }
            finally
            {
                _serverOperation = null;
                _launchingAccounts = false;
                LaunchActions.IsEnabled = true;
            }
        }

        private static string DescribeLaunchFailure(Exception ex)
        {
            if (ex is FormatException or IOException)
                return ex.Message;
            if (ex is WebView2RuntimeNotFoundException)
                return "Install the Microsoft WebView2 Runtime to use linked Roblox accounts.";
            string reason = ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name;
            App.Logger.WriteLine("LaunchMenu::AccountLaunch", $"Roblox request failed ({reason}).");
            return $"Finstrap could not prepare the Roblox launch ({reason}). Try again.";
        }
    }
}
