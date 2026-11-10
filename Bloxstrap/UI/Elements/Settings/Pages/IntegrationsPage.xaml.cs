using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

using Bloxstrap.Integrations;

using Bloxstrap.UI.ViewModels.Settings;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for IntegrationsPage.xaml
    /// </summary>
    public partial class IntegrationsPage
    {
        public Models.Persistable.Settings FinstrapSettings => App.Settings.Prop;

        public IntegrationsPage()
        {
            DataContext = new IntegrationsViewModel();
            InitializeComponent();
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            var profile = new SessionProfile { Name = "Roblox account" };
            FinstrapSettings.SessionProfiles.Add(profile);
            ProfilesList.SelectedItem = profile;
        }

        private void RemoveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is SessionProfile profile)
            {
                var groups = FinstrapSettings.LaunchGroups.Where(g => g.Accounts.Contains(profile.UserId)).ToArray();
                if (Frontend.ShowMessageBox($"Remove this account? It is referenced by: {string.Join(", ", groups.Select(g => g.Name))}", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                foreach (var group in groups) { group.Accounts.RemoveAll(id => id == profile.UserId); group.Layout.Remove(profile.UserId); }
                FinstrapSettings.SessionProfiles.Remove(profile);
            }
        }

        private void ChooseRecording_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is not SessionProfile profile)
                return;

            var dialog = new OpenFileDialog { Filter = "TinyTask recordings (*.rec)|*.rec" };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                TinyTaskRecording.Read(dialog.FileName);
                profile.MacroPath = dialog.FileName;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
            }
        }

        private void ClearRecording_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is SessionProfile profile)
            {
                profile.MacroPath = "";
            }
        }

        private void OpenProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is not SessionProfile profile)
                return;

            ProfileUserId.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            ProfilePlaceId.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            if (Validation.GetHasError(ProfileUserId) || Validation.GetHasError(ProfilePlaceId))
            {
                Frontend.ShowMessageBox("Enter numeric Roblox user and place IDs.", MessageBoxImage.Warning);
                return;
            }

            if (profile.UserId > 0 && FinstrapSettings.SessionProfiles.Count(item => item.UserId == profile.UserId) > 1)
            {
                Frontend.ShowMessageBox("Each account profile needs a different Roblox user ID.", MessageBoxImage.Warning);
                return;
            }

            try { profile.Open(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
            }
        }

        public void CustomIntegrationSelection(object sender, SelectionChangedEventArgs e)
        {
            IntegrationsViewModel viewModel = (IntegrationsViewModel)DataContext;
            viewModel.SelectedCustomIntegration = (CustomIntegration)((ListBox)sender).SelectedItem;
            viewModel.OnPropertyChanged(nameof(viewModel.SelectedCustomIntegration));
        }
    }
}
