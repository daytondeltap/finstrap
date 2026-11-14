using CommunityToolkit.Mvvm.ComponentModel;

namespace Bloxstrap.Models.Persistable
{
    public partial class SessionProfile : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";
        public string Username { get; set; } = "";
        public long UserId { get; set; }
        public long PlaceId { get; set; }
        public string? LaunchProfileKey { get; set; }
        [ObservableProperty]
        private string _macroPath = "";

        public void Open()
        {
            if (UserId <= 0 || PlaceId < 0)
                throw new InvalidOperationException("Enter a valid Roblox user ID and, optionally, an experience ID.");

            using var browser = Process.Start(AccountBrowser.CreateStartInfo(AccountBrowser.GetDefaultExecutable(), UserId, PlaceId));
        }
    }
}
