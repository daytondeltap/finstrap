namespace Bloxstrap.Models.Persistable
{
    public sealed class LaunchGroup
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Launch group";
        public List<long> Accounts { get; set; } = new();
        public string Destination { get; set; } = "";
        public Dictionary<long, SavedWindowLayout> Layout { get; set; } = new();
        public override string ToString() => Name;
    }
    public sealed class SavedWindowLayout
    {
        public string Monitor { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 0.5;
        public double Height { get; set; } = 0.5;
    }
}
