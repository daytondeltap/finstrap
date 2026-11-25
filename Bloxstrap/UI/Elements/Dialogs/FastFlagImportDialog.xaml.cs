using System.Windows;

namespace Bloxstrap.UI.Elements.Dialogs
{
    internal sealed record FastFlagImportRow(string Name, string Value, string Status)
    {
        public bool CanApply => Status is "New" or "Replace existing value";
    }

    public partial class FastFlagImportDialog
    {
        internal FastFlagImportDialog(IReadOnlyList<FastFlagImportRow> rows, string target)
        {
            InitializeComponent();
            Title = HeaderBar.Title = $"Inspect Fast Flag pack · {target}";
            int applicable = rows.Count(row => row.CanApply);
            Summary.Text = $"{applicable} of {rows.Count} entries will add or replace a value. Review the results below before applying.";
            Entries.ItemsSource = rows;
            ApplyButton.IsEnabled = applicable > 0;
        }

        private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
