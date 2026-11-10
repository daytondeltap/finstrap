using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bloxstrap.Integrations;
using Microsoft.Win32;

namespace Bloxstrap.UI.Elements.Dialogs
{
    internal sealed class AccountCard : INotifyPropertyChanged
    {
        public SessionProfile Profile { get; }
        public string Name => string.IsNullOrEmpty(Profile.Username) ? Profile.Name : $"{Profile.Name} (@{Profile.Username})";
        private string _detail = "Session status unknown";
        public string Detail { get => _detail; set { _detail = value; PropertyChanged?.Invoke(this,new(nameof(Detail))); } }
        private BitmapImage? _avatar;
        public BitmapImage? Avatar { get => _avatar; set { _avatar=value; PropertyChanged?.Invoke(this,new(nameof(Avatar))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public AccountCard(SessionProfile profile) => Profile=profile;
    }
    public partial class LaunchMenuDialog
    {
        private readonly AccountLaunchCoordinator Queue = new();
        private readonly CancellationTokenSource DashboardClosed = new();
        private readonly DispatcherTimer DashboardTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private List<SessionSnapshot> ActiveSessions = new();
        private readonly Dictionary<int,(long Started,TimeSpan Cpu,DateTime At)> CpuSamples = new();
        private bool Refreshing;
        private void InitializeDashboard()
        {
            Groups.ItemsSource = App.Settings.Prop.LaunchGroups;
            Queue.Changed += (id,state) => { foreach(var c in Cards.Where(c=>c.Profile.UserId==id)) c.Detail=state.ToString(); };
            DashboardTimer.Tick += async (_,_) => { if(IsVisible) await RefreshSessionsAsync(); };
            Closed += (_,_) => { DashboardTimer.Stop(); DashboardClosed.Cancel(); };
            Loaded += async (_,_) => { await RefreshSessionsAsync(); if(!SmokeMode) await LoadAvatarsAsync(); };
            DashboardTimer.Start();
        }
        private IEnumerable<AccountCard> Cards => AccountsList.Items.Cast<AccountCard>();
        private async Task RefreshSessionsAsync()
        {
            while (Refreshing && !DashboardClosed.IsCancellationRequested) await Task.Delay(25);
            if(DashboardClosed.IsCancellationRequested) return;
            Refreshing=true;
            try
            {
                var processes=Process.GetProcessesByName(App.RobloxPlayerAppName);
                int[] pids;
                try { pids=processes.Select(p=>p.Id).ToArray(); }
                finally { foreach(var p in processes) p.Dispose(); }
                var states=await Task.WhenAll(pids.Select(pid=>SessionChannel.ReadAsync(pid,DashboardClosed.Token)));
                ActiveSessions=states.OfType<SessionSnapshot>().ToList();
                foreach(var card in Cards)
                {
                    var session=ActiveSessions.FirstOrDefault(s=>s.UserId==card.Profile.UserId);
                    if(session is null)
                    {
                        card.Detail=Queue.States.TryGetValue(card.Profile.UserId,out var state) && state!=AccountLaunchState.Ready
                            ? state.ToString() : "No verified session · untracked clients may exist";
                        continue;
                    }
                    string resources="";
                    try
                    {
                        using var p=Process.GetProcessById(session.ProcessId);
                        if(p.StartTime.ToUniversalTime().Ticks!=session.StartedUtc) continue;
                        var now=DateTime.UtcNow; var cpu=p.TotalProcessorTime;
                        double percent=0;
                        if(CpuSamples.TryGetValue(p.Id,out var old) && old.Started==session.StartedUtc && now>old.At)
                            percent=Math.Max(0,(cpu-old.Cpu).TotalSeconds/(now-old.At).TotalSeconds/Environment.ProcessorCount*100);
                        CpuSamples[p.Id]=(session.StartedUtc,cpu,now);
                        resources=$" · {p.WorkingSet64/1048576} MB · CPU {percent:F1}%";
                    }
                    catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                    card.Detail=(session.InGame ? $"Joined game · place {session.PlaceId}" : "Client started · game unknown")+resources;
                }
                foreach(int pid in CpuSamples.Keys.Except(pids).ToArray()) CpuSamples.Remove(pid);
            }
            finally { Refreshing=false; }
        }
        private async Task LoadAvatarsAsync()
        {
            foreach(var card in Cards.ToArray())
            {
                if(DashboardClosed.IsCancellationRequested) return;
                try
                {
                    string directory=Path.Combine(Paths.Base,"AvatarCache"); Directory.CreateDirectory(directory);
                    string path=Path.Combine(directory,card.Profile.UserId+".png");
                    if(File.Exists(path))
                    {
                        try { card.Avatar=ReadAvatar(path); }
                        catch(Exception ex) when(ex is IOException or NotSupportedException or InvalidOperationException) { File.Delete(path); }
                    }
                    if(!File.Exists(path) || File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-7))
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(DashboardClosed.Token); timeout.CancelAfter(8000);
                        string json=await App.HttpClient.GetStringAsync($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={card.Profile.UserId}&size=150x150&format=Png&isCircular=false",timeout.Token);
                        using var doc=JsonDocument.Parse(json);
                        string? url=doc.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString();
                        if(Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.Host.EndsWith(".rbxcdn.com",StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] bytes=await App.HttpClient.GetByteArrayAsync(uri,timeout.Token);
                            if(bytes.Length>2_000_000) continue;
                            await File.WriteAllBytesAsync(path,bytes,timeout.Token); card.Avatar=ReadAvatar(path);
                        }
                    }
                }
                catch(Exception ex) when(ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException or NotSupportedException) { }
            }
        }
        private static BitmapImage ReadAvatar(string path)
        {
            var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.UriSource=new Uri(path); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        private void LinkAccount(SessionProfile? profile)
        {
            if(_launchingAccounts) return;
            var dialog=new AccountSignInDialog(profile,new AccountLaunchTarget(),linkOnly:true){Owner=this};
            if(dialog.ShowDialog()!=true || dialog.LinkedAccount is not {} identity) return;
            var saved=App.Settings.Prop.SessionProfiles.FirstOrDefault(p=>p.UserId==identity.Id);
            if(saved is null) { saved=new(){UserId=identity.Id,Name=identity.Username}; App.Settings.Prop.SessionProfiles.Add(saved); }
            saved.Username=identity.Username; saved.LaunchProfileKey=identity.Key;
            App.Settings.Save(); AccountStatus.Text=App.Settings.LastSaveSucceeded ? "Account linked." : "Account could not be saved. Reopen Finstrap before retrying.";
            RefreshAccounts(); _=LoadAvatarsAsync();
        }
        private AccountCard? Card(object sender)=>(sender as FrameworkElement)?.DataContext as AccountCard;
        private async void LaunchCard_Click(object sender,RoutedEventArgs e)
        { if(Card(sender) is {} c) await ConfirmAndLaunchAsync(new[]{c.Profile}); }
        private void ReconnectCard_Click(object sender,RoutedEventArgs e) { if(Card(sender) is {} c) LinkAccount(c.Profile); }
        private void ActOnSession(object sender,Action<SessionSnapshot> action)
        {
            try
            {
                if(Card(sender) is not {} c) return;
                var session=ActiveSessions.FirstOrDefault(s=>s.UserId==c.Profile.UserId);
                if(session is null) { AccountStatus.Text="No verified session is available for this account."; return; }
                action(session);
            }
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception) { AccountStatus.Text=ex.Message; }
        }
        private void FocusCard_Click(object sender,RoutedEventArgs e)=>ActOnSession(sender,SessionWindows.Focus);
        private void CloseCard_Click(object sender,RoutedEventArgs e)
        {
            if(Frontend.ShowMessageBox("Close this account's game window?",MessageBoxImage.Question,MessageBoxButton.YesNo)==MessageBoxResult.Yes)
                ActOnSession(sender,SessionWindows.Close);
        }
        private void RemoveCard_Click(object sender,RoutedEventArgs e)
        {
            if(_launchingAccounts || Card(sender) is not {} c) return;
            var affected=App.Settings.Prop.LaunchGroups.Where(g=>g.Accounts.Contains(c.Profile.UserId)).ToArray();
            if(Frontend.ShowMessageBox($"Remove {c.Name}? Running games stay open. Group references will be removed from: {string.Join(", ",affected.Select(g=>g.Name))}",MessageBoxImage.Question,MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
            foreach(var g in affected) { g.Accounts.RemoveAll(id=>id==c.Profile.UserId); g.Layout.Remove(c.Profile.UserId); }
            App.Settings.Prop.SessionProfiles.Remove(c.Profile); SaveDashboard(); RefreshAccounts();
        }
        private void SaveDashboard()
        {
            App.Settings.Save(); AccountStatus.Text=App.Settings.LastSaveSucceeded ? "Saved." : "Save failed. Reopen settings to preserve changes from other windows.";
        }
        private void GroupChanged(object sender,SelectionChangedEventArgs e)
        {
            if(Groups.SelectedItem is not LaunchGroup g) return;
            if(g.Version != 1) { AccountStatus.Text="This group was created by a newer Finstrap version."; return; }
            GroupName.Text=g.Name; AccountDestination.Text=g.Destination;
            var cards=Cards.ToArray();
            AccountsList.ItemsSource=g.Accounts.Select(id=>cards.FirstOrDefault(c=>c.Profile.UserId==id)).OfType<AccountCard>()
                .Concat(cards.Where(c=>!g.Accounts.Contains(c.Profile.UserId))).ToArray();
            AccountsList.SelectedItems.Clear();
            foreach(var c in Cards.Where(c=>g.Accounts.Contains(c.Profile.UserId)).Take(App.Settings.Prop.MultiInstance ? int.MaxValue : 1)) AccountsList.SelectedItems.Add(c);
        }
        private void SaveGroup_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                AccountLaunchTarget.Parse(AccountDestination.Text);
                var ids=Cards.Where(c=>AccountsList.SelectedItems.Contains(c)).Select(c=>c.Profile.UserId).ToList();
                if(ids.Count==0 || string.IsNullOrWhiteSpace(GroupName.Text)) { AccountStatus.Text="Enter a group name and select accounts."; return; }
                var group=Groups.SelectedItem as LaunchGroup;
                if(group is null || group.Name!=GroupName.Text.Trim()) { group=new(); App.Settings.Prop.LaunchGroups.Add(group); }
                group.Name=GroupName.Text.Trim(); group.Accounts=ids; group.Destination=AccountDestination.Text.Trim();
                SaveDashboard(); Groups.Items.Refresh(); Groups.SelectedItem=group;
            }
            catch(FormatException ex) { AccountStatus.Text=ex.Message; }
        }
        private void DeleteGroup_Click(object sender,RoutedEventArgs e)
        {
            if(Groups.SelectedItem is LaunchGroup g && Frontend.ShowMessageBox($"Delete group {g.Name}?",MessageBoxImage.Question,MessageBoxButton.YesNo)==MessageBoxResult.Yes)
            { App.Settings.Prop.LaunchGroups.Remove(g); SaveDashboard(); Groups.Items.Refresh(); }
        }
        private void Move(int offset)
        {
            if(_launchingAccounts || AccountsList.SelectedItem is not AccountCard c) return;
            var selected=AccountsList.SelectedItems.Cast<AccountCard>().ToArray(); var cards=Cards.ToList();
            int index=cards.IndexOf(c), next=index+offset; if(next<0 || next>=cards.Count)return;
            (cards[index],cards[next])=(cards[next],cards[index]); AccountsList.ItemsSource=cards;
            foreach(var item in selected) AccountsList.SelectedItems.Add(item);
        }
        private void MoveUp_Click(object sender,RoutedEventArgs e)=>Move(-1);
        private void MoveDown_Click(object sender,RoutedEventArgs e)=>Move(1);
        private void CancelRemaining_Click(object sender,RoutedEventArgs e)
        { Queue.CancelRemaining=true; AccountStatus.Text="Remaining launches cancelled; the current launch may finish."; }
        private async void RetryFailed_Click(object sender,RoutedEventArgs e)
        {
            if(_launchingAccounts)return;
            await RefreshSessionsAsync();
            if(Frontend.ShowMessageBox("Retry only if the failed account has no game window open. An untracked client cannot be identified safely. Continue?",MessageBoxImage.Question,MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            await ConfirmAndLaunchAsync(Cards.Where(c=>Queue.States.TryGetValue(c.Profile.UserId,out var state) && state==AccountLaunchState.Failed && !ActiveSessions.Any(s=>s.UserId==c.Profile.UserId)).Select(c=>c.Profile).ToArray());
        }
        private async void LaunchDefault_Click(object sender,RoutedEventArgs e)
        {
            if(_launchingAccounts)return;
            if(AccountsList.SelectedItems.Count>0) await ConfirmAndLaunchAsync(Cards.Where(c=>AccountsList.SelectedItems.Contains(c)).Select(c=>c.Profile).ToArray());
            else ((Bloxstrap.UI.ViewModels.Installer.LaunchMenuViewModel)DataContext).LaunchRobloxCommand.Execute(null);
        }
        private void Tile_Click(object sender,RoutedEventArgs e)
        {
            int count=ActiveSessions.Count; if(count==0)return;
            int cols=(int)Math.Ceiling(Math.Sqrt(count)), rows=(int)Math.Ceiling(count/(double)cols);
            for(int i=0;i<count;i++) TryLayout(ActiveSessions[i],new(){X=(i%cols)/(double)cols,Y=(i/cols)/(double)rows,Width=1.0/cols,Height=1.0/rows});
        }
        private void TryLayout(SessionSnapshot s,SavedWindowLayout l)
        { try { SessionWindows.Restore(s,l); } catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception) { AccountStatus.Text=ex.Message; } }
        private void SaveLayout_Click(object sender,RoutedEventArgs e)
        {
            if(Groups.SelectedItem is not LaunchGroup g) { AccountStatus.Text="Select a group first."; return; }
            try { foreach(var s in ActiveSessions.Where(s=>g.Accounts.Contains(s.UserId))) g.Layout[s.UserId]=SessionWindows.Capture(s); SaveDashboard(); }
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception) { AccountStatus.Text=ex.Message; }
        }
        private void RestoreSelectedLayout()
        { if(Groups.SelectedItem is LaunchGroup g) foreach(var s in ActiveSessions) if(g.Layout.TryGetValue(s.UserId,out var l)) TryLayout(s,l); }
        private async Task RestoreSelectedLayoutAsync()
        {
            if(Groups.SelectedItem is not LaunchGroup group) return;
            // Watcher readiness precedes window creation. Retry only while a window is not yet available.
            foreach(var session in ActiveSessions.ToArray())
            {
                if(!group.Layout.TryGetValue(session.UserId,out var layout)) continue;
                for(int attempt=0;attempt<20 && !DashboardClosed.IsCancellationRequested;attempt++)
                {
                    try { SessionWindows.Restore(session,layout); break; }
                    catch(InvalidOperationException ex)
                    { if(attempt==19) AccountStatus.Text=ex.Message; else await Task.Delay(250); }
                    catch(Exception ex) when(ex is ArgumentException or Win32Exception) { AccountStatus.Text=ex.Message; break; }
                }
            }
        }
        private async void RestoreLayout_Click(object sender,RoutedEventArgs e)=>await RestoreSelectedLayoutAsync();
        private void ExportDiagnostics_Click(object sender,RoutedEventArgs e)
        {
            var dialog=new SaveFileDialog{Filter="JSON (*.json)|*.json",FileName="Finstrap-diagnostics.json"};
            if(dialog.ShowDialog()!=true)return;
            try
            {
                // Explicit allowlist: never export configuration, logs, account IDs, destinations, or tickets.
                File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new { version=typeof(App).Assembly.GetName().Version?.ToString(),
                    os=Environment.OSVersion.VersionString, runtime=Environment.Version.ToString(),
                    sessions=ActiveSessions.Count, joined=ActiveSessions.Count(s=>s.InGame),
                    queue=Queue.States.Values.Select(s=>s.ToString()).ToArray() },new JsonSerializerOptions{WriteIndented=true}));
                AccountStatus.Text="Diagnostics exported without account identities or authentication data.";
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AccountStatus.Text=ex.Message;}
        }
    }
}
