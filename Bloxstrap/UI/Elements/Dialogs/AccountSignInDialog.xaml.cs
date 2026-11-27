using System.Windows;
using System.Windows.Threading;

using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountSignInDialog
    {
        private readonly SessionProfile? _profile;
        private readonly DispatcherTimer _signInTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly string _profileKey;
        private readonly CancellationTokenSource _closed = new();
        private readonly Action<long, string, string> _saveAccount;
        private bool _checking;
        private bool _autoCheck = true;

        internal AccountSignInDialog(SessionProfile? profile, Action<long, string, string> saveAccount)
        {
            _profile = profile;
            _saveAccount = saveAccount;
            _profileKey = profile?.LaunchProfileKey is string key && Guid.TryParseExact(key, "N", out _)
                ? key
                : Guid.NewGuid().ToString("N");
            InitializeComponent();
            _signInTimer.Tick += async (_, _) =>
            {
                if (_autoCheck)
                    await CheckAccountAsync();
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            _signInTimer.Stop();
            _closed.Cancel();
            Browser.Dispose();
            base.OnClosed(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                await RobloxWebProfile.InitializeAsync(Browser, _profileKey, _closed.Token);
                CoreWebView2 web = Browser.CoreWebView2;
                web.NavigationStarting += (_, args) => args.Cancel = !IsAllowedPage(args.Uri);
                web.NewWindowRequested += (_, args) => args.Handled = true;
                web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(RobloxWebProfile.Origin + "/login");
                _signInTimer.Start();
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (WebView2RuntimeNotFoundException)
            {
                StatusText.Text = "Install the Microsoft WebView2 Runtime to use account sign-in in Finstrap.";
            }
            catch (Exception)
            {
                StatusText.Text = "Finstrap could not open Roblox sign-in. Close this window and try again.";
            }
        }

        private static bool IsAllowedPage(string? address) =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "www.roblox.com" or "roblox.com";

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_checking || _closed.IsCancellationRequested)
                return;
            if (!e.IsSuccess)
            {
                StatusText.Text = "Roblox could not load. Close this window to retry.";
                return;
            }

            if (_autoCheck)
                await CheckAccountAsync();
        }

        private async void Continue_Click(object sender, RoutedEventArgs e) => await CheckAccountAsync();

        private async Task CheckAccountAsync()
        {
            if (_checking || _closed.IsCancellationRequested || Browser.Source is not Uri source || !IsAllowedPage(source.AbsoluteUri))
                return;

            _checking = true;
            ContinueButton.IsEnabled = false;
            try
            {
                using var client = await RobloxWebProfile.OpenClientAsync(Browser, _closed.Token);
                var identity = client is null ? null : await new RobloxAccountSession(client).GetIdentityAsync(_closed.Token);
                if (identity is null)
                {
                    StatusText.Text = _profile is null ? "Sign in to Roblox to link an account."
                        : $"Sign in to Roblox as {_profile.Name}.";
                    return;
                }

                if (_profile is not null && _profile.UserId != identity.Value.Id)
                {
                    _autoCheck = false;
                    StatusText.Text = $"This window is signed in as {identity.Value.Name}. Switch back to {_profile.Name}, then click Continue.";
                    return;
                }

                _saveAccount(identity.Value.Id, identity.Value.Name, _profileKey);
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _autoCheck = false;
                App.Logger.WriteLine("AccountSignIn", $"Roblox account verification failed ({ex.GetType().Name}).");
                StatusText.Text = ex is IOException ? "Finstrap could not save or verify this account. Click Continue to try again."
                    : "Roblox could not verify this sign-in. Finish signing in, then click Continue.";
            }
            finally
            {
                _checking = false;
                if (!_closed.IsCancellationRequested)
                    ContinueButton.IsEnabled = true;
            }
        }
    }
}
