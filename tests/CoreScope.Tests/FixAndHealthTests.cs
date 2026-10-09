using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreScope.Core;
using CoreScope.Core.Fixes;
using CoreScope.Core.Hardware;
using Xunit;

namespace CoreScope.Tests;

/// <summary>Records what the fix runner asked the machine to do, instead of doing it.</summary>
internal sealed class FakeTools : IToolRunner
{
    public List<string> Calls { get; } = new();
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";

    public (int ExitCode, string Output) Run(string file, string arguments, int timeoutMs = 20000)
    {
        Calls.Add($"{file} {arguments}");
        return (ExitCode, Output);
    }
}

internal sealed class FakeHost : IFixHost
{
    public bool Agree { get; set; } = true;
    public List<string> Questions { get; } = new();
    public List<string> Pages { get; } = new();
    public List<Guide> Guides { get; } = new();
    public int Unlocks { get; private set; }

    public List<string?> Previews { get; } = new();
    public bool Confirm(string title, string message, string yes, string? preview = null) { Questions.Add(message); Previews.Add(preview); return Agree; }
    public void ShowGuide(Guide guide) => Guides.Add(guide);
    public bool Navigate(string page) { Pages.Add(page); return page != "Nowhere"; }
    public void UnlockFullAccess() => Unlocks++;
}

public class FixRunnerTests
{
    private static (FakeTools Tools, FakeHost Host) Arrange()
    {
        var tools = new FakeTools();
        FixRunner.Tools = tools;
        return (tools, new FakeHost());
    }

    [Fact]
    public async Task Page_NavigatesInsideTheApp()
    {
        var (_, host) = Arrange();
        var ok = await FixRunner.RunAsync(FixButtons.Page("Open", "Control"), host);
        Assert.True(ok.Ok);
        Assert.Equal(new[] { "Control" }, host.Pages);
        Assert.False((await FixRunner.RunAsync(FixButtons.Page("Open", "Nowhere"), host)).Ok);
    }

    [Fact]
    public async Task Guide_IsHandedToTheHost()
    {
        var (_, host) = Arrange();
        var guide = FixButtons.EnableSecureBoot(null);
        await FixRunner.RunAsync(FixButtons.Steps("Show me", guide), host);
        Assert.Single(host.Guides);
        Assert.Equal(guide.Title, host.Guides[0].Title);
    }

    [Fact]
    public async Task ConfirmedCommand_RunsTheTool()
    {
        var (tools, host) = Arrange();
        var result = await FixRunner.RunAsync(FixButtons.Do(FixIds.EnableFirewall, "Turn on", "Sure?", admin: false), host);
        Assert.True(result.Ok);
        Assert.Equal(new[] { "Sure?" }, host.Questions);
        Assert.Equal(new[] { "netsh advfirewall set allprofiles state on" }, tools.Calls);
    }

    [Fact]
    public async Task Confirmation_ShowsWhatTheFixWillDo()
    {
        var (_, host) = Arrange();
        await FixRunner.RunAsync(FixButtons.Do(FixIds.EnableFirewall, "Turn on", "Sure?"), host);
        Assert.Contains("netsh advfirewall set allprofiles state on", host.Previews.Single());
    }

    [Fact]
    public async Task EveryCommandFix_AsksFirst_EvenWithoutItsOwnQuestion()
    {
        var (tools, host) = Arrange();
        host.Agree = false;
        var result = await FixRunner.RunAsync(FixButtons.Do(FixIds.SyncClock, "Sync clock"), host);   // no confirm text given
        Assert.False(result.Ok);
        Assert.Empty(tools.Calls);
        Assert.Single(host.Questions);
        Assert.Contains("w32tm", host.Previews.Single());
    }

    [Fact]
    public async Task NetworkSettingFix_ConfirmsWithPreviewBeforeChangingAnything()
    {
        var (tools, host) = Arrange();
        host.Agree = false;
        var result = await FixRunner.RunAsync(new FixAction(CoreScope.Core.Network.NetworkFixes.DisableRsc, "Turn off RSC", FixKind.Command), host);
        Assert.False(result.Ok);
        Assert.Empty(tools.Calls);
        Assert.Contains("rsc=disabled", host.Previews.Single());
    }

    [Fact]
    public void EveryCommandFix_HasAPreviewAndAQuestion()
    {
        var ids = typeof(FixIds).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)
            .Where(id => id != FixIds.Unlock);
        foreach (var id in ids)
        {
            var action = new FixAction(id, "x", FixKind.Command, "App", "Q?");
            Assert.False(string.IsNullOrWhiteSpace(FixPreviews.For(action)), "no preview for " + id);
        }
        foreach (var id in new[] { CoreScope.Core.Network.NetworkFixes.DisableRsc, CoreScope.Core.Network.NetworkFixes.EnableRsc, CoreScope.Core.Network.NetworkFixes.AutoTuningNormal, CoreScope.Core.Network.NetworkFixes.FlushDns })
            Assert.NotNull(FixPreviews.For(new FixAction(id, "x", FixKind.Command)));
        Assert.Null(FixPreviews.For(FixButtons.Page("Open", "Control")));
    }

    [Fact]
    public async Task DeclinedConfirmation_ChangesNothing()
    {
        var (tools, host) = Arrange();
        host.Agree = false;
        var result = await FixRunner.RunAsync(FixButtons.Do(FixIds.HibernateOff, "Off", "Really?"), host);
        Assert.False(result.Ok);
        Assert.Equal("Cancelled. Nothing was changed.", result.Message);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task AdminFix_WithoutAdmin_OffersToUnlockAndRunsNothing()
    {
        if (Native.IsAdministrator()) return;   // this environment already has full access
        var (tools, host) = Arrange();
        var result = await FixRunner.RunAsync(FixButtons.Do(FixIds.EnableTrim, "Turn TRIM on", admin: true), host);
        Assert.False(result.Ok);
        Assert.Contains("full access", result.Message);
        Assert.Equal(1, host.Unlocks);          // user agreed to the unlock prompt
        Assert.Empty(tools.Calls);

        host.Agree = false;
        await FixRunner.RunAsync(FixButtons.Do(FixIds.EnableTrim, "Turn TRIM on", admin: true), host);
        Assert.Equal(1, host.Unlocks);          // declined: no second unlock
    }

    [Fact]
    public async Task ToolFailure_IsReportedPlainly()
    {
        var (tools, host) = Arrange();
        tools.ExitCode = 5;
        tools.Output = "Access is denied.";
        var result = await FixRunner.RunAsync(FixButtons.Do(FixIds.EnableFirewall, "Turn on"), host);
        Assert.False(result.Ok);
        Assert.Contains("code 5", result.Message);
        Assert.Contains("Access is denied", result.Message);
    }

    [Fact]
    public async Task UnknownCommand_IsRefusedNotGuessed()
    {
        var (tools, host) = Arrange();
        var result = await FixRunner.RunAsync(FixButtons.Do("made.up", "Mystery"), host);
        Assert.False(result.Ok);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task RestartToUefi_AsksFirstAndStartsAnAbortableTimer()
    {
        var (tools, host) = Arrange();
        var action = FixButtons.RestartToUefi();
        var result = await FixRunner.RunAsync(action, host);
        Assert.True(result.Ok);
        Assert.Single(host.Questions);
        Assert.Contains("/r /fw /t 15", tools.Calls[0]);
        Assert.Contains("shutdown /a", result.Message);   // tells the user how to cancel
    }

    [Fact]
    public void EveryFixIdInTheCatalogIsImplemented()
    {
        // Each id must be handled by the runner (not fall through to "doesn't know that fix").
        var ids = typeof(FixIds).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(ids.Count >= 15);
        var (tools, host) = Arrange();
        foreach (var id in ids.Where(i => i is not (FixIds.Unlock or FixIds.CleanTemp or FixIds.EmptyRecycleBin or FixIds.MaxRefreshRate or FixIds.DisableStartupApp or FixIds.BalancedPower or FixIds.StorageSenseOn)))
        {
            var result = FixRunner.RunAsync(FixButtons.Do(id, id), host).GetAwaiter().GetResult();
            Assert.DoesNotContain("doesn't know", result.Message);
        }
    }
}


public class TempFilesTests
{
    [Fact]
    public void Clean_RemovesOnlyOldFilesAndKeepsRecentOnes()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corescope-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            var old = System.IO.Path.Combine(folder, "old.tmp");
            var fresh = System.IO.Path.Combine(folder, "fresh.tmp");
            System.IO.File.WriteAllText(old, new string('x', 1000));
            System.IO.File.WriteAllText(fresh, "y");
            System.IO.File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

            var (bytes, complete) = TempFiles.Measure(TimeSpan.FromSeconds(20));
            Assert.True(complete);
            Assert.True(bytes >= 1000);                       // at least our old file is counted

            TempFiles.Clean();
            Assert.False(System.IO.File.Exists(old));          // old: gone
            Assert.True(System.IO.File.Exists(fresh));         // under 24 h: kept
        }
        finally
        {
            try { System.IO.Directory.Delete(folder, true); } catch (System.IO.IOException) { }
        }
    }
}

public class HealthInsightTests
{
    private static SystemSpec BaseSpec(string build = "26100", string name = "Microsoft Windows 11 Pro")
    {
        var smbios = SmBios.Parse(new byte[] { 0, 3, 6, 0, 6, 0, 0, 0, 127, 4, 0, 0, 0, 0 }) ?? throw new InvalidOperationException();
        var spec = new SystemSpec { SmBios = smbios };
        spec.Os = new OsSpec { Name = name, Build = build, IsAdmin = true, PowerPlan = "Balanced" };
        spec.Volumes.Add(new VolumeSpec { Name = "C:", TotalBytes = 500_000_000_000, FreeBytes = 300_000_000_000 });
        return spec;
    }

    private static HealthSnapshot HealthyHealth() => new()
    {
        AntivirusActive = true, AntivirusName = "Microsoft Defender", RealTimeProtection = true, SignatureAgeDays = 0,
        FirewallDomain = true, FirewallPrivate = true, FirewallPublic = true, UacEnabled = true,
        RemoteDesktopOn = false, AutoSignIn = false, PendingReboot = false, DaysSinceUpdate = 5, UpdateServiceDisabled = false,
        SystemRestoreOn = true, DaysSinceRestorePoint = 10, ClockServiceRunning = true, SystemDriveEncrypted = true,
        TempBytes = 10 << 20, RecycleBinBytes = 5 << 20, StorageSenseOn = true, PageFileMissing = false,
        StartupApps = new() { "OneDrive", "Teams" }, RefreshHz = 60, MaxRefreshHz = 60,
    };

    private static HealthSnapshot UnhealthyHealth() => new()
    {
        AntivirusActive = false, FirewallPublic = false, FirewallPrivate = true, FirewallDomain = true, UacEnabled = false,
        RemoteDesktopOn = true, AutoSignIn = true, PendingReboot = true, DaysSinceUpdate = 200, UpdateServiceDisabled = true,
        SystemRestoreOn = false, ClockServiceRunning = false, SystemDriveEncrypted = false,
        TempBytes = 3L << 30, RecycleBinBytes = 4L << 30, HibernationFileBytes = 12L << 30, StorageSenseOn = false, PageFileMissing = true,
        StartupApps = Enumerable.Range(1, 15).Select(i => "App" + i).ToList(), RefreshHz = 60, MaxRefreshHz = 144,
    };

    [Fact]
    public void HealthyPc_ShowsPassedChecksForEveryAreaAndNoProblems()
    {
        var insights = new InsightEngine().Evaluate(BaseSpec("26200"), HealthyHealth());
        var good = insights.Where(i => i.Severity == Severity.Good).Select(i => i.Title).ToList();

        Assert.DoesNotContain(insights, i => i.Severity <= Severity.Warning);
        Assert.Contains(good, t => t.Contains("is on and up to date"));        // antivirus
        Assert.Contains(good, t => t.Contains("Windows Firewall is on"));
        Assert.Contains(good, t => t.Contains("User Account Control is on"));
        Assert.Contains(good, t => t.Contains("Windows updates are current"));
        Assert.Contains(good, t => t.Contains("No restart is pending"));
        Assert.Contains(good, t => t.Contains("System Restore is on"));
        Assert.Contains(good, t => t.Contains("clock keeps itself in sync"));
        Assert.Contains(good, t => t.Contains("Temporary files are under control"));
        Assert.Contains(good, t => t.Contains("Startup load is light"));
        Assert.Contains(good, t => t.Contains("refresh rate"));
        Assert.Contains(good, t => t.Contains("is supported until"));           // Windows lifecycle
        Assert.True(good.Count >= 12);
    }

    [Fact]
    public void UnhealthyPc_FindsEachProblemWithTheRightSeverityAndFix()
    {
        var spec = BaseSpec("22621");
        spec.Volumes[0] = new VolumeSpec { Name = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", TotalBytes = 500_000_000_000, FreeBytes = 50_000_000_000 };   // 10% free
        var insights = new InsightEngine().Evaluate(spec, UnhealthyHealth());
        Insight Find(string text) => insights.Single(i => i.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
        bool HasFix(Insight i, string id) => i.AllFixes.Any(f => f.Id == id);

        Assert.Equal(Severity.Critical, Find("No antivirus").Severity);
        Assert.Equal(Severity.Critical, Find("no longer gets security updates").Severity);   // Windows 11 22H2 ended in 2024
        Assert.Equal(Severity.Critical, Find("No Windows update was installed").Severity);
        Assert.Equal(Severity.Warning, Find("Windows Firewall is off for Public").Severity);
        Assert.True(HasFix(Find("Windows Firewall is off"), FixIds.EnableFirewall));
        Assert.True(HasFix(Find("waiting for a restart"), FixIds.Restart));
        Assert.True(HasFix(Find("update service is disabled"), FixIds.StartWindowsUpdate));
        Assert.True(HasFix(Find("System Restore is off"), FixIds.EnableRestore));
        Assert.True(HasFix(Find("clock isn't syncing"), FixIds.SyncClock));
        Assert.True(HasFix(Find("temporary files"), FixIds.CleanTemp));
        Assert.True(HasFix(Find("Recycle Bin holds"), FixIds.EmptyRecycleBin));
        Assert.True(HasFix(Find("hibernation file"), FixIds.HibernateOff));
        Assert.True(HasFix(Find("Hz but supports"), FixIds.MaxRefreshRate));
        Assert.Equal(Severity.Warning, Find("start with Windows").Severity);                 // 15 startup programs
        Assert.Contains(Find("start with Windows").AllFixes, f => f.Kind == FixKind.Page);
        Assert.Contains(Find("User Account Control is off").AllFixes, f => f.Kind == FixKind.Settings);
    }

    [Fact]
    public void EveryWarningAndCriticalFinding_HasAWayForward()
    {
        var spec = BaseSpec("22621");
        spec.Volumes[0] = new VolumeSpec { Name = "C:", TotalBytes = 500_000_000_000, FreeBytes = 10_000_000_000 };
        spec.Os.PowerPlan = "Power saver";
        spec.Os.LastBoot = DateTime.Now.AddDays(40);
        spec.Os.TrimEnabled = false;
        spec.Os.IsAdmin = false;
        spec.Disks.Add(new DiskSpec { Name = "Disk A", MediaType = "SSD", Health = "Warning", SizeBytes = 500_000_000_000 });
        spec.Board.SecureBoot = false;
        spec.Board.TpmPresent = false;
        spec.Board.BiosDate = DateTime.Now.AddYears(-6);
        spec.Cpu.VirtualizationFirmwareEnabled = false;
        spec.Gpus.Add(new GpuSpec { Name = "NVIDIA GeForce RTX", Vendor = "NVIDIA", DriverDate = DateTime.Now.AddYears(-3) });
        spec.Battery = new BatterySpec { DesignMwh = 50000, FullChargeMwh = 25000 };

        var engine = new InsightEngine();
        var hot = new SensorReading("/c/t", "cpu", "cpu", "Cpu", "CPU Package", "Temperature", 97, 97, 97);
        for (var i = 0; i < 20; i++) engine.Observe(new[] { hot });

        var insights = engine.Evaluate(spec, UnhealthyHealth());
        var stuck = insights.Where(i => i.Severity <= Severity.Warning && !i.HasFixes).Select(i => i.Title).ToList();
        Assert.True(insights.Count(i => i.Severity <= Severity.Warning) >= 15);
        Assert.Empty(stuck);   // nothing serious without a button
    }

    [Fact]
    public void FixButtons_AreWellFormed()
    {
        var insights = new InsightEngine().Evaluate(BaseSpec("22621"), UnhealthyHealth());
        foreach (var fix in insights.SelectMany(i => i.AllFixes))
        {
            Assert.False(string.IsNullOrWhiteSpace(fix.Label), fix.Id);
            Assert.False(string.IsNullOrWhiteSpace(fix.Id));
            if (fix.Kind is FixKind.Settings or FixKind.Url or FixKind.Page) Assert.False(string.IsNullOrWhiteSpace(fix.Target), fix.Id);
            if (fix.Kind == FixKind.Guide) { Assert.NotNull(fix.Guide); Assert.True(fix.Guide!.Steps.Count >= 3); }
            if (fix.Kind == FixKind.Url) Assert.StartsWith("https://", fix.Target);
            if (fix.Kind == FixKind.Command && fix.Id is FixIds.EmptyRecycleBin or FixIds.HibernateOff or FixIds.CleanTemp or FixIds.EnableFirewall or FixIds.CreateRestorePoint)
                Assert.False(string.IsNullOrWhiteSpace(fix.Confirm), "destructive or system-wide fixes must confirm first: " + fix.Id);
        }
    }

    [Fact]
    public void WindowsSupport_WarnsInTheLastMonthsAndPassesWhenFarOff()
    {
        // 26200 (25H2) is supported well into the future for Home/Pro.
        var good = new InsightEngine().Evaluate(BaseSpec("26200", "Microsoft Windows 11 Pro"), HealthyHealth());
        Assert.Contains(good, i => i.Severity == Severity.Good && i.Title.Contains("25H2"));
        // Windows 10 22H2 ended in October 2025.
        var ended = new InsightEngine().Evaluate(BaseSpec("19045", "Microsoft Windows 10 Pro"), HealthyHealth());
        Assert.Contains(ended, i => i.Severity == Severity.Critical && i.Title.Contains("Windows 10 22H2"));
    }

    [Fact]
    public void UnknownHealth_StaysSilentInsteadOfGuessing()
    {
        // A snapshot where nothing could be read produces no security / update findings at all.
        var insights = new InsightEngine().Evaluate(BaseSpec(), new HealthSnapshot());
        Assert.DoesNotContain(insights, i => i.Category == "Security");
        Assert.DoesNotContain(insights, i => i.Title.Contains("update", StringComparison.OrdinalIgnoreCase) && i.Severity <= Severity.Warning);
    }

    [Fact]
    public void Guides_ExplainTheStepsAndOfferHelp()
    {
        var xmp = FixButtons.EnableMemoryProfile("https://example.com/support", 3200, 2133);
        Assert.Contains("3200", xmp.Intro);
        Assert.True(xmp.Steps.Count >= 5);
        Assert.Contains(xmp.Actions, a => a.Id == FixIds.RestartToUefi && a.Confirm is not null);
        Assert.Contains(xmp.Actions, a => a.Kind == FixKind.Url);
    }
}

public class HealthScoreTests
{
    private static Insight Of(Severity s) => new(s, "System", "x" + Guid.NewGuid(), "d");

    [Fact]
    public void ManyTipsCannotMakeAPcLookBroken()
    {
        var tips = Enumerable.Range(0, 30).Select(_ => Of(Severity.Info)).ToList();
        Assert.Equal(90, CoreScope.Core.ViewModels.InsightsViewModel.Score(tips));
    }

    [Fact]
    public void ProblemsCostMoreThanTips()
    {
        var items = new[] { Of(Severity.Critical), Of(Severity.Warning), Of(Severity.Warning), Of(Severity.Info), Of(Severity.Good) };
        Assert.Equal(100 - 25 - 20 - 1, CoreScope.Core.ViewModels.InsightsViewModel.Score(items));
    }

    [Fact]
    public void ScoreNeverGoesBelowZero() =>
        Assert.Equal(0, CoreScope.Core.ViewModels.InsightsViewModel.Score(Enumerable.Range(0, 10).Select(_ => Of(Severity.Critical))));
}

public class FixButtonTests
{
    [Fact]
    public void ControlCenterButtonsCanTargetASection()
    {
        Assert.Equal("Control", FixButtons.ControlCenter("Open").Target);
        Assert.Equal("Control|Startup apps", FixButtons.ControlCenter("Open", false, "Startup apps").Target);
    }

    [Fact]
    public void EveryFindingExposesOneButtonPerFix()
    {
        var insight = new Insight(Severity.Warning, "Windows", "t", "d") { Fixes = new[] { FixButtons.WindowsUpdate(), FixButtons.Restart() } };
        Assert.Equal(2, insight.Buttons.Count);
        Assert.All(insight.Buttons, b => Assert.Same(insight, b.Owner));
        Assert.True(insight.HasFixes);
        Assert.False(new Insight(Severity.Good, "Windows", "t", "d").HasFixes);
    }

    [Fact]
    public void NetworkFixKeyBecomesTheFirstButton()
    {
        var insight = new Insight(Severity.Warning, "Network", "Slow", "d") { FixKey = CoreScope.Core.Network.NetworkFixes.FlushDns, Fixes = new[] { FixButtons.Open("More", "ms-settings:network") } };
        Assert.Equal(2, insight.AllFixes.Count);
        Assert.Equal(CoreScope.Core.Network.NetworkFixes.FlushDns, insight.AllFixes[0].Id);
        Assert.True(insight.AllFixes[0].Primary);
    }

    [Fact]
    public void FixStateStartsIdleAndReportsChanges()
    {
        var state = new FixState();
        var seen = new List<string?>();
        state.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.True(state.IsIdle);
        state.IsBusy = true;
        state.Result = "Done";
        Assert.False(state.IsIdle);
        Assert.True(state.HasResult);
        Assert.Contains("IsIdle", seen);
        Assert.Contains("HasResult", seen);
    }
}
