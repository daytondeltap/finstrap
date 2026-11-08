using CommunityToolkit.Mvvm.ComponentModel;

namespace Bloxstrap.Models.Persistable
{
    public partial class SessionProfile : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";
        public long UserId { get; set; }
        public long PlaceId { get; set; }
        [ObservableProperty]
        private string _macroPath = "";

        public void Open()
        {
            if (UserId <= 0 || PlaceId < 0)
                throw new InvalidOperationException("Enter a valid Roblox user ID and, optionally, an experience ID.");

            string edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe"))
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Microsoft Edge is required for account profiles.");

            var start = new ProcessStartInfo(edge);
            start.ArgumentList.Add($"--user-data-dir={Path.Combine(Paths.Base, "AccountProfiles", UserId.ToString(CultureInfo.InvariantCulture))}");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(PlaceId > 0 ? $"https://www.roblox.com/games/{PlaceId}" : "https://www.roblox.com/home");
            using var browser = Process.Start(start);
        }
    }
}
