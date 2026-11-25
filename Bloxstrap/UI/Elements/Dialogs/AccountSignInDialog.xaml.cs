using System.Windows;
using System.Windows.Threading;

using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountSignInDialog
    {
        private static readonly RobloxDatacenters Datacenters = new(App.HttpClient);
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

        private long? _verifiedUserId;
        private readonly Action<long, string, string> _saveAccount;
        private readonly bool _linkOnly;
        private readonly bool _inspectServer;
        private readonly Func<AccountLaunchTarget, Task<string>>? _homePlay;
        private bool _homeLaunching;
        private readonly string? _country;
        internal ServerLocation? Location { get; private set; }
        internal string? FailureReason { get; private set; }
        internal RobloxAccountLaunch? Result { get; private set; }

        internal AccountSignInDialog(SessionProfile? profile, AccountLaunchTarget target,
            Action<long, string, string> saveAccount, bool linkOnly = false,
            string? country = null, bool inspectServer = false, Func<AccountLaunchTarget, Task<string>>? homePlay = null)
        {
            _saveAccount = saveAccount;
            _homePlay = homePlay;
            _linkOnly = linkOnly;
            _country = country;
            _inspectServer = inspectServer;
            _profile = profile;
            _target = target;
            _profileKey = profile?.LaunchProfileKey is string key && Guid.TryParseExact(key, "N", out _)
                ? key
                : Guid.NewGuid().ToString("N");
            _signInStarted = _profileKey != profile?.LaunchProfileKey;
            InitializeComponent();
            if (_homePlay is not null)
            {
                Title = $"Roblox Home — {_profile?.Name} ({_country})";
                ContinueButton.Content = "Play this page";
                ShowHomeStatus();
            }
            _signInTimer.Tick += async (_, _) =>
            {
                if (_autoCheck && _signInStarted)
                    await CheckAccountAsync();
            };
            if (!_signInStarted && !_linkOnly && _homePlay is null)
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
                web.LaunchingExternalUriScheme += (_, args) =>
                {
                    args.Cancel = true;
                    if (_homePlay is null || !args.IsUserInitiated || !IsAllowedPage(args.InitiatingOrigin))
                        return;
                    if (!args.Uri.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase) &&
                        !args.Uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
                        return;

                    string url = args.Uri;
                    Dispatcher.BeginInvoke(new Action(async () => await PlayFromHomeAsync(url, isProtocol: true)));
                };
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(RobloxOrigin + (_signInStarted ? "/login" : "/home"));
                if (_homePlay is null)
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

        private static bool IsAllowedPage(string? address)
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

            if (_homePlay is not null)
            {
                ContinueButton.IsEnabled = !_homeLaunching && Browser.Source is Uri page &&
                    Regex.IsMatch(page.AbsolutePath, @"\A/games/[0-9]+(?:/|\z)");
                if (!_homeLaunching)
                    ShowHomeStatus();
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

        private async void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (_homePlay is not null && Browser.Source is { } page)
                await PlayFromHomeAsync(page.AbsoluteUri, isProtocol: false);
            else
                await CheckAccountAsync();
        }

        private void ShowHomeStatus() => ShowStatus($"Server country: {_country}. Open a game and press Play or use Play this page.");

        private async Task PlayFromHomeAsync(string url, bool isProtocol)
        {
            if (_homePlay is null || _homeLaunching || _closed.IsCancellationRequested)
                return;

            _homeLaunching = true;
            ContinueButton.IsEnabled = false;
            try
            {
                var target = isProtocol ? ServerRegionLookup.ParseBrowserPlay(url) : AccountLaunchTarget.Parse(url);
                if (target is null || target.PlaceId <= 0)
                    throw new FormatException("Open a public game page to use the country filter.");
                ShowStatus($"Finding a server in {_country}…");
                ShowStatus(await _homePlay(target));
            }
            catch (FormatException ex) { ShowStatus(ex.Message); }
            finally
            {
                _homeLaunching = false;
                if (!_closed.IsCancellationRequested)
                    ContinueButton.IsEnabled = Browser.Source is Uri page && Regex.IsMatch(page.AbsolutePath, @"\A/games/[0-9]+(?:/|\z)");
            }
        }

        private async Task CheckAccountAsync()
        {
            if (_checking || _closed.IsCancellationRequested || Browser.Source is not Uri source || !IsAllowedPage(source.AbsoluteUri))
                return;

            _checking = true;
            FailureReason = null;
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
                if ((_profile?.UserId ?? _verifiedUserId) is long expectedId && expectedId != userId)
                {
                    _autoCheck = false;
                    ShowStatus($"This window is signed in as {username}. Switch back to {_profile?.Name ?? "the linked account"}, then click Continue.");
                    return;
                }

                try { _saveAccount(userId, username, _profileKey); }
                catch (IOException)
                {
                    ShowLaunchError("Finstrap could not save this account. Reopen Finstrap and try again.");
                    return;
                }
                _verifiedUserId = userId;
                if (_linkOnly)
                {
                    DialogResult = true;
                    return;
                }
                var target = _target;
                if (_inspectServer || _country is not null)
                {
                    ShowStatus("Checking public server locations…");
                    Location = await new ServerRegionLookup(App.HttpClient, session, Datacenters).FindAsync(target, _country, _closed.Token);
                    if (_inspectServer)
                    {
                        DialogResult = true;
                        return;
                    }
                    target = target with { JobId = Location.JobId.ToString("D") };
                }
                else
                {
                    target = await session.ResolveShareAsync(target, _closed.Token);
                }
                string ticket = await session.GetTicketAsync(_closed.Token);
                Result = new RobloxAccountLaunch(userId, username, _profileKey, ticket, target, Location);
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (FormatException ex)
            {
                ShowLaunchError("Account saved. " + ex.Message);
            }
            catch (Exception ex)
            {
                string reason = ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name;
                App.Logger.WriteLine("AccountSignIn", $"Roblox sign-in or launch request failed ({reason}).");
                ShowLaunchError(_verifiedUserId.HasValue
                    ? $"Account saved. Roblox could not start the desktop session ({reason}). Click Continue to retry, or close and launch from your saved accounts later."
                    : $"Roblox could not verify this sign-in ({reason}). Finish signing in and click Continue to retry.");
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
            FailureReason = message;
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
