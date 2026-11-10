using System.Windows;
using System.Windows.Threading;

using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountSignInDialog
    {
        private const string RobloxOrigin = "https://www.roblox.com";
        private readonly SessionProfile? _profile;
        private readonly AccountLaunchTarget _target;
        private readonly DispatcherTimer _signInTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly string _profileKey;
        private readonly CancellationTokenSource _closed = new();
        private CoreWebView2Environment? _environment;
        private bool _checking;
        private bool _autoCheck = true;
        private bool _signInStarted;

        internal (long Id, string Username, string Key)? LinkedAccount { get; private set; }
        private readonly bool _linkOnly;
        internal RobloxAccountLaunch? Result { get; private set; }

        internal AccountSignInDialog(SessionProfile? profile, AccountLaunchTarget target, bool linkOnly = false)
        {
            _linkOnly = linkOnly;
            _profile = profile;
            _target = target;
            _profileKey = profile?.LaunchProfileKey is string key && Guid.TryParseExact(key, "N", out _)
                ? key
                : Guid.NewGuid().ToString("N");
            _signInStarted = _profileKey != profile?.LaunchProfileKey;
            InitializeComponent();
            _signInTimer.Tick += async (_, _) =>
            {
                if (_autoCheck && _signInStarted)
                    await CheckAccountAsync();
            };
            if (!_signInStarted && !_linkOnly)
            {
                Opacity = 0;
                ShowActivated = false;
            }
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
                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(Paths.Base, "AccountProfiles", "FinstrapSignIn"))
                    .WaitAsync(TimeSpan.FromSeconds(15), _closed.Token);
                if (_closed.IsCancellationRequested)
                    return;

                var options = _environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = _profileKey;
                await Browser.EnsureCoreWebView2Async(_environment, options)
                    .WaitAsync(TimeSpan.FromSeconds(15), _closed.Token);
                if (_closed.IsCancellationRequested)
                    return;

                CoreWebView2 web = Browser.CoreWebView2;
                web.Settings.IsPasswordAutosaveEnabled = false;
                web.Settings.IsGeneralAutofillEnabled = false;
                web.Settings.IsWebMessageEnabled = false;
                web.Settings.AreDevToolsEnabled = false;
                web.NavigationStarting += (_, args) => args.Cancel = !IsAllowedPage(args.Uri);
                web.NewWindowRequested += (_, args) => args.Handled = true;
                web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(RobloxOrigin + (_signInStarted ? "/login" : "/home"));
                _signInTimer.Start();

                await Task.Delay(TimeSpan.FromSeconds(12), _closed.Token);
                if (Opacity == 0)
                    ShowStatus("Roblox is taking longer to respond. You can wait, retry, or close this window.");
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowStatus("Install the Microsoft WebView2 Runtime to use account sign-in in Finstrap.");
            }
            catch (Exception)
            {
                ShowStatus("Finstrap could not open Roblox sign-in. Close this window and try again.");
            }
        }

        private bool IsAllowedPage(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                return false;
            return uri.Host is "www.roblox.com" or "roblox.com";
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_checking || _closed.IsCancellationRequested)
                return;
            if (!e.IsSuccess)
            {
                ShowStatus("Roblox could not load. Close this window to retry.");
                return;
            }

            ContinueButton.IsEnabled = true;
            if (Browser.Source?.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase) == true)
            {
                _autoCheck = true;
                _signInStarted = true;
                ShowStatus(_profile is null ? "Sign in on Roblox to add and launch this account."
                    : $"Sign in to {_profile.Name} on Roblox to launch this account.");
            }
            else if (_autoCheck)
            {
                await CheckAccountAsync();
            }
        }

        private async void Continue_Click(object sender, RoutedEventArgs e) => await CheckAccountAsync();

        private async Task CheckAccountAsync()
        {
            if (_checking || _closed.IsCancellationRequested || Browser.Source?.Host != "www.roblox.com")
                return;

            _checking = true;
            ContinueButton.IsEnabled = false;
            try
            {
                // Read this WebView profile's cookies without navigating away from sign-in.
                // Redirects are disabled so authentication cannot be forwarded to another host.
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                foreach (var cookie in await Browser.CoreWebView2.CookieManager.GetCookiesAsync(RobloxOrigin)
                    .WaitAsync(TimeSpan.FromSeconds(10), _closed.Token))
                {
                    if (cookie.Name == ".ROBLOSECURITY")
                        handler.CookieContainer.Add(new Cookie(cookie.Name, cookie.Value, "/", ".roblox.com")
                            { Secure = true, HttpOnly = true });
                }
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var session = new RobloxAccountSession(client);
                var identity = await session.GetIdentityAsync(_closed.Token);
                if (identity is null)
                {
                    ShowStatus("Finish signing in and verifying your account on the Roblox page.");
                    if (!_signInStarted)
                    {
                        _signInStarted = true;
                        Browser.CoreWebView2.Navigate(RobloxOrigin + "/login");
                    }
                    return;
                }

                long userId = identity.Value.Id;
                string username = identity.Value.Name;
                if (userId <= 0 || String.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException();
                if (_profile is not null && _profile.UserId != userId)
                {
                    _autoCheck = false;
                    ShowStatus($"This window is signed in as {username}. Switch to {_profile.Name} in Roblox, then click Continue.");
                    return;
                }

                LinkedAccount = (userId, username, _profileKey);
                if (_linkOnly)
                {
                    DialogResult = true;
                    return;
                }
                var target = await session.ResolveShareAsync(_target, _closed.Token);
                string ticket = await session.GetTicketAsync(_closed.Token);
                Result = new RobloxAccountLaunch(userId, username, _profileKey, ticket, target);
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (FormatException ex)
            {
                ShowLaunchError(ex.Message);
            }
            catch (Exception)
            {
                ShowLaunchError("Roblox did not complete the desktop sign-in handoff. Check your sign-in and click Continue to retry.");
            }
            finally
            {
                _checking = false;
                if (!_closed.IsCancellationRequested)
                    ContinueButton.IsEnabled = true;
            }
        }

        private void ShowLaunchError(string message)
        {
            _autoCheck = false;
            if (!_closed.IsCancellationRequested && Browser.Source?.Host != "www.roblox.com")
                Browser.CoreWebView2.Navigate(RobloxOrigin + "/home");
            ShowStatus(message);
        }

        private void ShowStatus(string message)
        {
            if (_closed.IsCancellationRequested)
                return;
            StatusText.Text = message;
            if (Opacity == 0)
            {
                Opacity = 1;
                Activate();
            }
        }
    }
}
