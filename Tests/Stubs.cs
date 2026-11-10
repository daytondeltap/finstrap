global using System.Text;
global using System.Diagnostics;
global using System.Text.Json;
global using System.Text.RegularExpressions;
global using System.Globalization;
global using System.Net;
global using Bloxstrap.Utility;
global using Bloxstrap.Resources;
global using Bloxstrap;

namespace Bloxstrap
{
    internal static class App
    {
        public static string RobloxPlayerAppName => Process.GetCurrentProcess().ProcessName;
        public const string ProjectName = "Finstrap-Regression";
        public static readonly TestLogger Logger = new();
        public static readonly HttpClient HttpClient = new();
    }
    internal sealed class TestLogger
    {
        public void WriteLine(string source, string message) { }
        public void WriteException(string source, Exception exception) { }
    }
    internal static class Paths { public static string Base = ""; }
    internal class Settings { }
    internal class FastFlagManager { }
    internal static class Frontend
    {
        public static int Warnings;
        public static void ShowMessageBox(string message, System.Windows.MessageBoxImage image) => Warnings++;
    }
}
namespace Bloxstrap.Resources
{
    internal static class Strings
    {
        public const string JsonManager_SettingsLoadFailed = "Settings failed";
        public const string JsonManager_FastFlagsLoadFailed = "Flags failed";
        public const string Bootstrapper_JsonManagerSaveFailed = "{0}: {1}";
    }
}
namespace System.Windows { public enum MessageBoxImage { Warning } }
namespace Bloxstrap.Integrations
{
    internal static class RobloxPlayerProcesses
    {
        public static bool CloseVerifiedTrayProcesses() => false;
    }
}
