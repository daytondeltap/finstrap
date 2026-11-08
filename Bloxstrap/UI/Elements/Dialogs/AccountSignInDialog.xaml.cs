using System.Windows;
using System.Windows.Threading;

using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountSignInDialog
    {
        private const string RobloxOrigin = "https://www.roblox.com";
        private const string TicketUrl = "https://auth.roblox.com/v1/authentication-ticket/";
        private const string ShareUrl = "https://apis.roblox.com/sharelinks/v1/resolve-link";
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
        private readonly AccountLaunchTarget _target;
        private readonly DispatcherTimer _signInTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly string _profileKey;
        private readonly CancellationTokenSource _closed = new();
        private CoreWebView2Environment? _environment;
        private bool _checking;
        private bool _autoCheck = true;
        private bool _signInStarted;
        private string? _requestUrl;

        internal RobloxAccountLaunch? Result { get; private set; }

        internal AccountSignInDialog(SessionProfile? profile, AccountLaunchTarget target)
        {
            _profile = profile;
            _target = target;
            _signInStarted = profile?.LaunchProfileKey is null;
            _profileKey = profile?.LaunchProfileKey is string key && Guid.TryParseExact(key, "N", out _)
                ? key
                : Guid.NewGuid().ToString("N");
            InitializeComponent();
            _signInTimer.Tick += async (_, _) =>
            {
                if (_autoCheck && _signInStarted)
                    await CheckAccountAsync();
            };
            if (profile?.LaunchProfileKey is not null)
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
                web.AddWebResourceRequestedFilter(TicketUrl, CoreWebView2WebResourceContext.Document,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                web.AddWebResourceRequestedFilter(ShareUrl, CoreWebView2WebResourceContext.Document,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                web.WebResourceRequested += SetRequestOrigin;
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

        private void SetRequestOrigin(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            // Set these after Chromium prepares the native navigation request.
            if (e.Request.Uri != _requestUrl || e.Request.Method != "POST")
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
                uri.AbsoluteUri == _requestUrl;
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
                string request = JsonSerializer.Serialize(new { expression = ReadIdentity, awaitPromise = true, returnByValue = true });
                string json = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request)
                    .WaitAsync(TimeSpan.FromSeconds(10), _closed.Token);
                using var response = JsonDocument.Parse(json);
                if (!response.RootElement.GetProperty("result").TryGetProperty("value", out var identity))
                    throw new InvalidOperationException();
                if (identity.ValueKind == JsonValueKind.Null)
                {
                    ShowStatus("Finish signing in and verifying your account on the Roblox page.");
                    if (!_signInStarted)
                    {
                        _signInStarted = true;
                        Browser.CoreWebView2.Navigate(RobloxOrigin + "/login");
                    }
                    return;
                }

                long userId = identity.GetProperty("id").GetInt64();
                string username = identity.GetProperty("name").GetString() ?? "";
                if (userId <= 0 || String.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException();
                if (_profile is not null && _profile.UserId != userId)
                {
                    _autoCheck = false;
                    ShowStatus($"This window is signed in as {username}. Switch to {_profile.Name} in Roblox, then click Continue.");
                    return;
                }

                AccountLaunchTarget target = _target;
                if (target.ShareCode is not null)
                {
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { linkId = target.ShareCode, linkType = "Server" });
                    var share = await PostAsAccountAsync(ShareUrl, payload);
                    if (share.Status is < 200 or >= 300 || share.Body is null)
                        throw new FormatException("Roblox could not open this private-server link for this account. Check access and whether the link is still active.");
                    target = AccountLaunchTarget.FromPrivateShare(share.Body);
                }

                var launch = await PostAsAccountAsync(TicketUrl, null);
                if (launch.Status != 200 || String.IsNullOrEmpty(launch.Ticket))
                    throw new InvalidOperationException();
                Result = new RobloxAccountLaunch(userId, username, _profileKey, launch.Ticket, target);
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

        private async Task<RobloxResponse> PostAsAccountAsync(string url, byte[]? payload)
        {
            _requestUrl = url;
            Browser.IsEnabled = false;
            try
            {
                var response = await RequestAsync(url, payload, null);
                if (response.Status == 403 && !String.IsNullOrEmpty(response.Csrf))
                    response = await RequestAsync(url, payload, response.Csrf);
                return response;
            }
            finally
            {
                _requestUrl = null;
                if (!_closed.IsCancellationRequested)
                    Browser.IsEnabled = true;
            }
        }

        private sealed record RobloxResponse(int Status, string? Csrf, string? Ticket, string? Body);

        private async Task<RobloxResponse> RequestAsync(string url, byte[]? payload, string? csrf)
        {
            CoreWebView2 web = Browser.CoreWebView2;
            var completion = new TaskCompletionSource<RobloxResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.WebResourceResponseReceived += OnResponse;
            try
            {
                string headers = payload is null ? "RBXAuthenticationNegotiation: 1\r\n" : "Content-Type: application/json\r\n";
                if (csrf is not null && !csrf.Contains('\r') && !csrf.Contains('\n'))
                    headers += $"X-CSRF-TOKEN: {csrf}\r\n";
                using var body = new MemoryStream(payload ?? Array.Empty<byte>());
                web.NavigateWithWebResourceRequest(_environment!.CreateWebResourceRequest(url, "POST", body, headers));
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), _closed.Token);
            }
            finally
            {
                web.WebResourceResponseReceived -= OnResponse;
            }

            async void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
            {
                if (e.Request.Method != "POST" || e.Request.Uri != url)
                    return;
                try
                {
                    var headers = e.Response.Headers;
                    string? content = null;
                    if (payload is not null && e.Response.StatusCode is >= 200 and < 300)
                    {
                        using var stream = await e.Response.GetContentAsync();
                        if (stream is not null)
                        {
                            using var reader = new StreamReader(stream);
                            content = await reader.ReadToEndAsync();
                        }
                    }
                    completion.TrySetResult(new(e.Response.StatusCode,
                        headers.Contains("x-csrf-token") ? headers.GetHeader("x-csrf-token") : null,
                        payload is null && headers.Contains("rbx-authentication-ticket")
                            ? headers.GetHeader("rbx-authentication-ticket") : null, content));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
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
