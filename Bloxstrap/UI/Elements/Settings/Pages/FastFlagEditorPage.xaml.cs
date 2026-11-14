using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.ObjectModel;

using Wpf.Ui.Mvvm.Contracts;

using Bloxstrap.UI.Elements.Dialogs;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for FastFlagEditorPage.xaml
    /// </summary>
    public partial class FastFlagEditorPage
    {
        private readonly ObservableCollection<FastFlag> _fastFlagList = new();
        private bool IsStudio => TargetSelector?.SelectedIndex == 1;
        private FastFlagManager Flags => IsStudio ? App.StudioFastFlags : App.FastFlags;

        private bool _showPresets = false;
        private string _searchFilter = "";

        public FastFlagEditorPage()
        {
            InitializeComponent();
        }

        private void ReloadList()
        {
            if (DataGrid is null)
                return;

            var selectedEntry = DataGrid.SelectedItem as FastFlag;

            _fastFlagList.Clear();

            var presetFlags = FastFlagManager.PresetFlags.Values;

            foreach (var pair in Flags.Prop.OrderBy(x => x.Key))
            {
                if (!IsStudio && !_showPresets && presetFlags.Contains(pair.Key))
                    continue;

                if (!pair.Key.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = new FastFlag
                {
                    Name = pair.Key,
                    Value = pair.Value?.ToString() ?? ""
                };

                _fastFlagList.Add(entry);
            }

            if (DataGrid.ItemsSource is null)
                DataGrid.ItemsSource = _fastFlagList;

            if (selectedEntry is null)
                return;

            var newSelectedEntry = _fastFlagList.Where(x => x.Name == selectedEntry.Name).FirstOrDefault();

            if (newSelectedEntry is null)
                return;
            
            DataGrid.SelectedItem = newSelectedEntry;
            DataGrid.ScrollIntoView(newSelectedEntry);
        }

        private void ClearSearch(bool refresh = true)
        {
            SearchTextBox.Text = "";
            _searchFilter = "";

            if (refresh)
                ReloadList();
        }

        private void ShowAddDialog()
        {
            var dialog = new AddFastFlagDialog();
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();

            if (dialog.Result != MessageBoxResult.OK)
                return;

            if (dialog.Tabs.SelectedIndex == 0)
                AddSingle(dialog.FlagNameTextBox.Text.Trim(), dialog.FlagValueTextBox.Text);
            else if (dialog.Tabs.SelectedIndex == 1)
                ImportJSON(dialog.JsonTextBox.Text);
        }

        private void AddSingle(string name, string value)
        {
            FastFlag? entry;

            if (Flags.GetValue(name) is null)
            {
                if (!ValidateFlagEntry(name, value))
                    return;

                entry = new FastFlag
                {
                    Name = name,
                    Value = value
                };

                if (!name.Contains(_searchFilter))
                    ClearSearch();

                _fastFlagList.Add(entry);

                Flags.SetValue(entry.Name, entry.Value);
            }
            else
            {
                Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Information);

                bool refresh = false;

                if (!IsStudio && !_showPresets && FastFlagManager.PresetFlags.Values.Contains(name))
                {
                    TogglePresetsButton.IsChecked = true;
                    _showPresets = true;
                    refresh = true;
                }

                if (!name.Contains(_searchFilter))
                {
                    ClearSearch(false);
                    refresh = true;
                }

                if (refresh)
                    ReloadList();

                entry = _fastFlagList.Where(x => x.Name == name).FirstOrDefault();
            }

            DataGrid.SelectedItem = entry;
            if (entry is not null)
                DataGrid.ScrollIntoView(entry);
        }

        private void ImportJSON(string json)
        {
            var rows = new List<FastFlagImportRow>();
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Expected a JSON object with flag names as keys.");

                var entries = document.RootElement.EnumerateObject().ToArray();
                foreach (var group in entries.GroupBy(entry => entry.Name, StringComparer.Ordinal))
                {
                    var entry = group.First();
                    var value = entry.Value.ToString();
                    bool scalar = entry.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
                    string status = group.Count() > 1 ? "Duplicate key"
                        : !scalar || FastFlagManager.GetValidationError(entry.Name, value) is not null ? "Invalid name or value"
                        : !IsStudio && !FastFlagManager.PlayerAllowlist.Contains(entry.Name) ? "Outside Player allowlist"
                        : Flags.GetValue(entry.Name) is null ? "New"
                        : Flags.GetValue(entry.Name) == value ? "Unchanged"
                        : "Replace existing value";
                    rows.Add(new FastFlagImportRow(entry.Name, value, status));
                }
            }
            catch (JsonException ex)
            {
                Frontend.ShowMessageBox(string.Format(Strings.Menu_FastFlagEditor_InvalidJSON, ex.Message), MessageBoxImage.Error);
                return;
            }

            var preview = new FastFlagImportDialog(rows, IsStudio ? "Studio" : "Player") { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true)
                return;

            foreach (var row in rows.Where(row => row.CanApply))
                Flags.SetValue(row.Name, row.Value);

            ClearSearch();
        }

        private bool ValidateFlagEntry(string name, string value)
        {
            string? error = FastFlagManager.GetValidationError(name, value);
            if (error is not null)
            {
                Frontend.ShowMessageBox(string.Format(error, name), MessageBoxImage.Error);
                return false;
            }

            if (!IsStudio && !FastFlagManager.PlayerAllowlist.Contains(name))
            {
                Frontend.ShowMessageBox("This flag is outside the published Player allowlist. Select Studio to edit its Studio configuration.", MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        // refresh list on page load to synchronize with preset page
        private void Page_Loaded(object sender, RoutedEventArgs e) => ReloadList();

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
                return;

            if (e.Row.DataContext is not FastFlag entry)
                return;

            if (e.EditingElement is not TextBox textbox)
                return;

            switch (((DataGridTextColumn)e.Column).SortMemberPath)
            {
                case nameof(FastFlag.Name):
                    string oldName = entry.Name;
                    string newName = textbox.Text;

                    if (newName == oldName)
                        return;

                    if (Flags.GetValue(newName) is not null)
                    {
                        Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Information);
                        e.Cancel = true;
                        textbox.Text = oldName;
                        return;
                    }

                    if (!ValidateFlagEntry(newName, entry.Value))
                    {
                        e.Cancel = true;
                        textbox.Text = oldName;
                        return;
                    }

                    Flags.SetValue(oldName, null);
                    Flags.SetValue(newName, entry.Value);

                    Dispatcher.BeginInvoke(new Action(ReloadList));

                    entry.Name = newName;

                    break;

                case nameof(FastFlag.Value):
                    string oldValue = entry.Value;
                    string newValue = textbox.Text;

                    if (!ValidateFlagEntry(entry.Name, newValue))
                    {
                        e.Cancel = true;
                        textbox.Text = oldValue;
                        return;
                    }

                    Flags.SetValue(entry.Name, newValue);

                    break;
            }
        }

        private void TargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataGrid is null)
                return;

            DataGrid.CancelEdit(DataGridEditingUnit.Row);
            ReloadList();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is INavigationWindow window)
                window.Navigate(typeof(FastFlagsPage));
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => ShowAddDialog();

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var tempList = new List<FastFlag>();

            foreach (FastFlag entry in DataGrid.SelectedItems)
                tempList.Add(entry);

            foreach (FastFlag entry in tempList)
            {
                _fastFlagList.Remove(entry);
                Flags.SetValue(entry.Name, null);
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button)
                return;

            _showPresets = button.IsChecked ?? false;
            ReloadList();
        }

        private void CopyJSONButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(ExportJson());
            Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_JsonCopiedToClipboard, MessageBoxImage.Information);
        }

        private string ExportJson() => JsonSerializer.Serialize(Flags.Prop, new JsonSerializerOptions { WriteIndented = true });

        private void ExportJSONButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = IsStudio ? "StudioClientAppSettings.json" : "ClientAppSettings.json"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, ExportJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textbox)
                return;

            _searchFilter = textbox.Text;
            ReloadList();
        }
    }
}
