using Bloxstrap.Integrations;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bloxstrap.UI.Elements.Dialogs
{
    internal static class RobloxWebProfile
    {
        internal const string Origin = "https://www.roblox.com";

        internal static async Task InitializeAsync(WebView2 view, string profileKey, CancellationToken token)
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Paths.Base, "AccountProfiles", "FinstrapSignIn"))
                .WaitAsync(TimeSpan.FromSeconds(15), token);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = profileKey;
            await view.EnsureCoreWebView2Async(environment, options).WaitAsync(TimeSpan.FromSeconds(15), token);
            view.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            view.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            view.CoreWebView2.Settings.IsWebMessageEnabled = false;
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        }

        internal static async Task<HttpClient?> OpenClientAsync(WebView2 view, CancellationToken token)
        {
            var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(Origin)
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            var cookie = cookies.Find(item => item.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(item.Value));
            if (cookie is null)
                return null;

            // Keep cookies in memory and prevent redirects from forwarding authentication.
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            try
            {
                handler.CookieContainer.Add(new Cookie(cookie.Name, cookie.Value, "/", ".roblox.com")
                    { Secure = true, HttpOnly = true });
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            }
            catch
            {
                handler.Dispose();
                throw;
            }
        }
    }
}
