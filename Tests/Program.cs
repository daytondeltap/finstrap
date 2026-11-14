using Bloxstrap.Integrations;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
Paths.Base = Path.Combine(Path.GetTempPath(), "finstrap-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Paths.Base);
try
{
    var fit = WindowLayoutMath.Fit(.75,.9,.5,.5,-1920,0,1920,1080);
    Check(fit.X == -960 && fit.Y == 540 && fit.Width == 960, "layouts clamp to monitor bounds including negative origins");
    fit = WindowLayoutMath.Fit(double.NaN,double.PositiveInfinity,double.MaxValue,-1,0,0,800,600);
    Check(fit.X == 0 && fit.Y == 0 && fit.Width == 800 && fit.Height == 240, "invalid layout fractions cannot position windows offscreen");
    using (var stop = new CancellationTokenSource())
    using (var self = Process.GetCurrentProcess())
    {
        var snapshot = new SessionSnapshot(self.Id,self.StartTime.ToUniversalTime().Ticks,123,true,456);
        var server = SessionChannel.ServeAsync(self.Id, () => snapshot,stop.Token);
        var received = await SessionChannel.ReadAsync(self.Id,default);
        Check(received == snapshot,"watcher channel roundtrip preserves verified process and account identity");
        snapshot = snapshot with { StartedUtc = 1 };
        Check(await SessionChannel.ReadAsync(self.Id,default) is null,"watcher channel rejects recycled or mismatched process identity");
        stop.Cancel();
        try { await server; } catch(OperationCanceledException) { }
    }
    var queue = new AccountLaunchCoordinator();
    queue.Begin(new long[] { 1, 2, 3 });
    queue.Set(1, AccountLaunchState.Ready);
    queue.Set(2, AccountLaunchState.Failed);
    queue.CancelRemaining = true;
    queue.Finish();
    Check(queue.States[1] == AccountLaunchState.Ready && queue.States[2] == AccountLaunchState.Failed && queue.States[3] == AccountLaunchState.Cancelled, "cancellation preserves successful and failed accounts");
    queue.Begin(new long[] { 2 });
    Check(queue.States[1] == AccountLaunchState.Ready && queue.States[2] == AccountLaunchState.Queued && !queue.CancelRemaining, "retry queues failed account without restarting successful account");
    queue.Finish();
    Check(queue.States[2] == AccountLaunchState.Failed, "uncompleted launch is not reported ready");
    var group = new Bloxstrap.Models.Persistable.LaunchGroup { Name = "Daily", Accounts = new() { 23, 12 }, Destination = "456" };
    group.Layout[23] = new() { Monitor = "display", X = .5, Width = .5 };
    var roundtrip = JsonSerializer.Deserialize<Bloxstrap.Models.Persistable.LaunchGroup>(JsonSerializer.Serialize(group))!;
    Check(roundtrip.Id == group.Id && roundtrip.Version == 1 && roundtrip.Accounts.SequenceEqual(new long[] { 23, 12 }) && roundtrip.Layout[23].X == .5, "launch group order, identity and layout survive serialization");
    var settings = new JsonManager<TestSettings>();
    settings.Load();
    Check(!settings.HasUnsavedChanges, "new settings start clean");
    settings.Prop.MultiInstance = true;
    settings.Prop.Profiles.Add(new() { UserId = 123, ProfileKey = "linked-profile" });
    Check(settings.HasUnsavedChanges, "toggles and account additions are tracked");
    settings.Save();
    Check(settings.LastSaveSucceeded && !settings.HasUnsavedChanges, "successful save clears pending changes");
    var reopened = new JsonManager<TestSettings>();
    reopened.Load();
    Check(reopened.Prop.MultiInstance && reopened.Prop.Profiles.Single().ProfileKey == "linked-profile", "settings and account linking survive restart");
    reopened.Prop.Profiles[0].ProfileKey = "relinked-profile";
    Check(reopened.HasUnsavedChanges, "nested profile edits are tracked");
    reopened.Save();
    settings.Load();
    Check(settings.Prop.Profiles[0].ProfileKey == "relinked-profile", "replacement save persists profile edits");
    Check(!Directory.GetFiles(Paths.Base, "*.tmp").Any(), "successful saves leave no temporary files");
    var stale = new JsonManager<TestSettings>();
    stale.Load();
    settings.Prop.Profiles.Add(new() { UserId = 456, ProfileKey = "second-account" });
    settings.Save();
    stale.Prop.MultiInstance = false;
    stale.Save();
    Check(!stale.LastSaveSucceeded && stale.HasUnsavedChanges, "stale settings window cannot overwrite newly linked accounts");
    reopened.Load();
    Check(reopened.Prop.MultiInstance && reopened.Prop.Profiles.Count == 2, "conflicting save preserves the current file");
    Frontend.Warnings = 0;
    File.WriteAllText(Path.Combine(Paths.Base, "blocked"), "not a directory");
    var blocked = new BlockedSettings();
    blocked.Prop.MultiInstance = true;
    blocked.Save();
    Check(!blocked.LastSaveSucceeded && blocked.HasUnsavedChanges && Frontend.Warnings == 1, "directory failures report failure and preserve dirty state");

    var handler = new FakeHandler();
    using var client = new HttpClient(handler);
    var session = new RobloxAccountSession(client);
    handler.Responses.Enqueue(new(HttpStatusCode.Unauthorized));
    Check(await session.GetIdentityAsync(default) is null, "signed-out account requires login");
    handler.Responses.Enqueue(Json("{\"id\":123,\"name\":\"Example\"}"));
    Check(await session.GetIdentityAsync(default) == (123L, "Example"), "account identity is verified");
    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
    challenge.Headers.Add("x-csrf-token", "csrf-test");
    const string assertion = "{\"clientAssertion\":\"signed-assertion\"}";
    handler.Responses.Enqueue(Json(assertion));
    handler.Responses.Enqueue(challenge);
    var ticket = new HttpResponseMessage(HttpStatusCode.OK);
    ticket.Headers.Add("rbx-authentication-ticket", "test-ticket-1234567890");
    handler.Responses.Enqueue(ticket);
    Check(await session.GetTicketAsync(default) == "test-ticket-1234567890", "CSRF challenge is retried and ticket extracted");
    Check(handler.Requests[^3].Path == "/v1/client-assertion/" && handler.Requests[^2].Body == assertion &&
        handler.Requests[^1].Body == assertion && handler.Requests[^1].Csrf == "csrf-test" &&
        handler.Requests[^1].Origin == "https://www.roblox.com", "ticket request and CSRF retry include Roblox's signed client assertion");
    handler.Responses.Enqueue(new(HttpStatusCode.NotFound));
    handler.Responses.Enqueue(new(HttpStatusCode.Forbidden));
    try { await session.GetTicketAsync(default); throw new Exception("Expected rejection"); }
    catch (HttpRequestException) { Check(true, "rejected ticket does not launch an account"); }
    handler.Responses.Enqueue(new(HttpStatusCode.NotFound));
    handler.Responses.Enqueue(new(HttpStatusCode.OK));
    try { await session.GetTicketAsync(default); throw new Exception("Expected missing ticket"); }
    catch (InvalidDataException) { Check(true, "missing launch ticket is rejected"); }
    handler.Responses.Enqueue(Json("{\"data\":{\"placeId\":456,\"linkCode\":\"private-code\"}}"));
    var target = await session.ResolveShareAsync(new(ShareCode: "share-code"), default);
    Check(target.PlaceId == 456 && target.LinkCode == "private-code", "private-server share resolves for the account");
    Check(handler.Requests[^1].Body!.Contains("share-code"), "share request sends the intended invite");
    int beforeRetry = handler.Requests.Count;
    handler.Responses.Enqueue(new(HttpStatusCode.NotFound));
    for (int i = 0; i < 2; i++)
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden);
        denied.Headers.Add("x-csrf-token", "retry-token");
        handler.Responses.Enqueue(denied);
    }
    try { await session.GetTicketAsync(default); throw new Exception("Expected rejection"); }
    catch (HttpRequestException) { Check(handler.Requests.Count == beforeRetry + 3, "CSRF retries stop after one retry"); }
    handler.Responses.Enqueue(Json("{\"id\":0,\"name\":\"\"}"));
    try { await session.GetIdentityAsync(default); throw new Exception("Expected invalid identity"); }
    catch (InvalidDataException) { Check(true, "invalid identity cannot link an account"); }
    var home = new RobloxAccountLaunch(123, "Example", "profile", "ticket:signature+/=", new());
    Check(home.OpenDesktopHome && home.Arguments.Contains("launchmode:app"), "home launches retain desktop mode");
    Check(home.Arguments.Contains("+gameinfo:ticket%3Asignature%2B%2F%3D+"), "opaque desktop tickets cannot split the launch protocol");
    var game = new RobloxAccountLaunch(123, "Example", "profile", "test-ticket-1234567890", target);
    Check(!game.OpenDesktopHome && Uri.UnescapeDataString(game.Arguments).Contains("RequestPrivateGame"), "private game launch includes resolved destination");
    Check(AccountLaunchTarget.Parse("https://www.roblox.com/games/456/Test")?.PlaceId == 456, "game links parse");
    try { AccountLaunchTarget.Parse("https://evil.example/games/456"); throw new Exception("Expected rejection"); }
    catch (FormatException) { Check(true, "untrusted destination host is rejected"); }

    using (var first = new MultiInstanceLease())
    {
        using (var second = new MultiInstanceLease())
            Check(Mutex.TryOpenExisting("ROBLOX_singletonMutex", out var legacy) && Dispose(legacy), "overlapping launch and watcher leases coexist");
        Check(Mutex.TryOpenExisting("ROBLOX_singletonEvent", out var current) && Dispose(current), "one lease survives another being disposed");
    }
    Check(!Mutex.TryOpenExisting("ROBLOX_singletonEvent", out _), "last lease releases singleton reservation");
    if (OperatingSystem.IsWindows())
    {
        using var conflict = new EventWaitHandle(false, EventResetMode.ManualReset, "ROBLOX_singletonEvent");
        try { using var lease = MultiInstanceLease.AcquireForLaunch(); throw new Exception("Expected collision"); }
        catch (WaitHandleCannotBeOpenedException) { Check(true, "existing Roblox event blocks unsafe lease acquisition"); }
    }
    Console.WriteLine($"{passed} regression checks passed.");
}
finally { Directory.Delete(Paths.Base, recursive: true); }

static bool Dispose(IDisposable? value) { value?.Dispose(); return true; }
static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
public class TestSettings
{
    public bool MultiInstance { get; set; }
    public List<TestProfile> Profiles { get; set; } = new();
}
public class TestProfile { public long UserId { get; set; } public string ProfileKey { get; set; } = ""; }
public class BlockedSettings : JsonManager<TestSettings>
{
    public override string FileLocation => Path.Combine(Paths.Base, "blocked", "Settings.json");
}
public class FakeHandler : HttpMessageHandler
{
    public Queue<HttpResponseMessage> Responses = new();
    public List<(string? Csrf, string Path, string? Origin, string? Body)> Requests = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        Requests.Add((Header("X-CSRF-TOKEN"), request.RequestUri!.AbsolutePath, Header("Origin"),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
        return Responses.Dequeue();
    }
}
