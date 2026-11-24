using System.Windows;
using System.Windows.Controls;
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
            if (!App.Settings.LastSaveSucceeded)
            {
                App.Settings.Prop.PreferredServerCountry = previous;
                _countryReady = false;
                ServerCountry.SelectedValue = previous ?? "";
                _countryReady = true;
                AccountStatus.Text = "Could not save the server country. Reopen Finstrap and try again.";
            }
        }

        internal void SetBrowserDestination(AccountLaunchTarget target)
        {
            string place = target.PlaceId.ToString(CultureInfo.InvariantCulture);
            AccountDestination.Text = target.JobId is null ? place :
                $"https://www.roblox.com/games/{place}/?gameInstanceId={target.JobId}";
            AccountStatus.Text = "Select the linked account you want to play with, then launch in your selected country.";
        }

        private void CheckServerLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_launchingAccounts)
                return;

            var profile = (AccountsList.SelectedItem as AccountCard)?.Profile;
            try
            {
                var target = AccountLaunchTarget.Parse(AccountDestination.Text) ?? new(PlaceId: profile?.PlaceId ?? 0);
                if (target.PlaceId <= 0)
                    throw new FormatException("Enter a public game or server link to check its location.");
                var dialog = new AccountSignInDialog(profile, target,
                    (id, name, key) => SaveVerifiedAccount(id, name, key, target),
                    country: target.JobId is null ? SelectedServerCountry : null,
                    inspectServer: true) { Owner = this };
                if (dialog.ShowDialog() == true && dialog.Location is { } location)
                    AccountStatus.Text = $"Estimated server location: {location}";
                if (profile is null)
                    RefreshAccounts();
            }
            catch (FormatException ex)
            {
                AccountStatus.Text = ex.Message;
            }
        }
    }
}
