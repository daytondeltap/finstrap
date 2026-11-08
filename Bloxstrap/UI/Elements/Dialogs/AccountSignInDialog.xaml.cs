using System.Windows;

using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountSignInDialog
    {
        private const string RobloxOrigin = "https://www.roblox.com";
        private const string TicketUrl = "https://auth.roblox.com/v1/authentication-ticket/";
        private const string ReadIdentity = @"(async () => {
            if (location.origin !== 'https://www.roblox.com') return null;
            const response = await fetch('https://users.roblox.com/v1/users/authenticated', {
                credentials: 'include', redirect: 'error', signal: AbortSignal.timeout(8000)
            });
            if (response.status === 401) return null;
            if (!response.ok) throw new Error('Roblox account request failed');
            const user = await response.json();
            return { id: user.id, name: user.name };
        })()";

        private readonly SessionProfile? _profile;
        private readonly string _profileKey;
        private readonly CancellationTokenSource _closed = new();
        private CoreWebView2Environment? _environment;
        private bool _checking;
        private bool _autoCheck = true;
        private bool _requestingTicket;

        internal RobloxAccountLaunch? Result { get; private set; }

        internal AccountSignInDialog(SessionProfile? profile)
        {
            _profile = profile;
            _profileKey = profile?.LaunchProfileKey is string key && Guid.TryParseExact(key, "N", out _)
                ? key
                : Guid.NewGuid().ToString("N");
            InitializeComponent();
            if (profile?.LaunchProfileKey is not null)
            {
                Opacity = 0;
                ShowActivated = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
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
                web.AddWebResourceRequestedFilter(TicketUrl, CoreWebView2WebResourceContext.Document,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                web.WebResourceRequested += SetTicketRequestOrigin;
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(RobloxOrigin + (_profile?.LaunchProfileKey is null ? "/login" : "/home"));

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

        private void SetTicketRequestOrigin(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            // Set these after Chromium prepares the native navigation request.
            if (!_requestingTicket || e.Request.Uri != TicketUrl || e.Request.Method != "POST")
                return;
            e.Request.Headers.SetHeader("Origin", RobloxOrigin);
            e.Request.Headers.SetHeader("Referer", RobloxOrigin + "/");
        }

        private bool IsAllowedPage(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                return false;
            return uri.Host is "www.roblox.com" or "roblox.com" ||
                (_requestingTicket && uri.AbsoluteUri == TicketUrl);
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
                string request = JsonSerializer.Serialize(new { expression = ReadIdentity, awaitPromise = true, returnByValue = true });
                string json = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request)
                    .WaitAsync(TimeSpan.FromSeconds(10), _closed.Token);
                using var response = JsonDocument.Parse(json);
                if (!response.RootElement.GetProperty("result").TryGetProperty("value", out var identity))
                    throw new InvalidOperationException();
                if (identity.ValueKind == JsonValueKind.Null)
                {
                    ShowStatus("Sign in on the Roblox page to continue.");
                    if (Browser.Source.AbsolutePath != "/login")
                        Browser.CoreWebView2.Navigate(RobloxOrigin + "/login");
                    return;
                }

                long userId = identity.GetProperty("id").GetInt64();
                string username = identity.GetProperty("name").GetString() ?? "";
                if (userId <= 0 || String.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException();
                if (_profile is not null && _profile.UserId != userId)
                {
                    ShowStatus($"This window is signed in as {username}. Switch to {_profile.Name} in Roblox, then click Continue.");
                    return;
                }

                string ticket = await GetLaunchTicketAsync();
                Result = new RobloxAccountLaunch(userId, username, _profileKey, ticket);
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
            catch (Exception)
            {
                _autoCheck = false;
                if (!_closed.IsCancellationRequested && Browser.Source?.Host == "auth.roblox.com")
                    Browser.CoreWebView2.Navigate(RobloxOrigin + "/home");
                ShowStatus("Roblox did not complete the desktop sign-in handoff. Check your sign-in and click Continue to retry.");
            }
            finally
            {
                _checking = false;
                if (!_closed.IsCancellationRequested)
                    ContinueButton.IsEnabled = true;
            }
        }

        private async Task<string> GetLaunchTicketAsync()
        {
            _requestingTicket = true;
            Browser.IsEnabled = false;
            try
            {
                var response = await RequestTicketAsync(null);
                if (response.Status == 403 && !String.IsNullOrEmpty(response.Csrf))
                    response = await RequestTicketAsync(response.Csrf);
                if (response.Status != 200 || String.IsNullOrEmpty(response.Ticket))
                    throw new InvalidOperationException();
                return response.Ticket;
            }
            finally
            {
                _requestingTicket = false;
                if (!_closed.IsCancellationRequested)
                    Browser.IsEnabled = true;
            }
        }

        private async Task<(int Status, string? Csrf, string? Ticket)> RequestTicketAsync(string? csrf)
        {
            CoreWebView2 web = Browser.CoreWebView2;
            var completion = new TaskCompletionSource<(int, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.WebResourceResponseReceived += OnResponse;
            try
            {
                string headers = "RBXAuthenticationNegotiation: 1\r\n";
                if (csrf is not null && !csrf.Contains('\r') && !csrf.Contains('\n'))
                    headers += $"X-CSRF-TOKEN: {csrf}\r\n";
                using var body = new MemoryStream();
                web.NavigateWithWebResourceRequest(_environment!.CreateWebResourceRequest(TicketUrl, "POST", body, headers));
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), _closed.Token);
            }
            finally
            {
                web.WebResourceResponseReceived -= OnResponse;
            }

            void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
            {
                if (e.Request.Method != "POST" || e.Request.Uri != TicketUrl)
                    return;
                var headers = e.Response.Headers;
                completion.TrySetResult((e.Response.StatusCode,
                    headers.Contains("x-csrf-token") ? headers.GetHeader("x-csrf-token") : null,
                    headers.Contains("rbx-authentication-ticket") ? headers.GetHeader("rbx-authentication-ticket") : null));
            }
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
