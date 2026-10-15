using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppleMusicPresence.Bench;
using Windows.Media.Control;

// Out-of-process benchmark for the published tray app. It only observes the app, signals the
// same "show" event a second launch would, and posts WM_CLOSE (the window's own close-to-tray
// path). It never writes settings, touches the startup entry or controls media playback.
// Benchmarked instances use the real saved settings and publish the real current song.
const string ShowEventName = @"Local\AppleMusicPresence.Show";
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
Native.TimeBeginPeriod(1);

return args switch
{
    ["memmap", var pid, ..] => Write(MemoryMapOf(int.Parse(pid, CultureInfo.InvariantCulture)), args.ElementAtOrDefault(2)),
    ["scenario", .. var rest] => await ScenarioAsync(new Options(rest)),
    ["soak", .. var rest] => await SoakAsync(new Options(rest)),
    ["summarize", var directory] => SummarizeDirectory(directory),
    ["watch", var pid, var seconds] => await WatchAsync(int.Parse(pid, CultureInfo.InvariantCulture), double.Parse(seconds, CultureInfo.InvariantCulture)),
    ["capture", .. var rest] => await CaptureAsync(new Options(rest)),
    _ => Usage()
};

int Usage()
{
    Console.Error.WriteLine("""
        memmap <pid> [out.json]
        scenario --exe <path> --label <name> --out <dir> [--warmup 20] [--steady 60] [--hidden 20] [--counters <dotnet-counters.exe>]
        soak --exe <path> --label <name> --out <dir> [--minutes 15] [--open-at <seconds>] [--counters <dotnet-counters.exe>]
        capture --exe <path> --out <file.png> [--delay 8]
        summarize <dir>
        watch <pid> <seconds>
        """);
    return 2;
}

int Write(object value, string? path)
{
    var text = JsonSerializer.Serialize(value, jsonOptions);
    if (path is null) Console.WriteLine(text);
    else { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, text); }
    return 0;
}

// Saves the visible window as the user would see it, to compare rendering between builds.
async Task<int> CaptureAsync(Options options)
{
    var exe = Path.GetFullPath(options.Get("exe"));
    var output = Path.GetFullPath(options.Get("out"));
    Native.SetProcessDpiAwarenessContext(-4); // per-monitor v2, so window sizes are physical pixels
    EnsureNoInstance();
    var clock = Stopwatch.StartNew();
    using var process = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false })!;
    try
    {
        using var probe = new ProcessProbe(process.Id);
        var launchedAt = clock.Elapsed;
        double Since() => (clock.Elapsed - launchedAt).TotalSeconds;
        await StartupAsync(process, clock, launchedAt);
        await Task.Delay(TimeSpan.FromSeconds(options.Seconds("delay", 8)));
        var open = await OpenAsync(probe, Since);
        await Task.Delay(TimeSpan.FromSeconds(2));
        WindowCapture.SavePng(open.Window, output);
        await HideAsync(probe, open.Window);
        Console.WriteLine($"captured {output}");
        return 0;
    }
    finally { Stop(process); }
}

async Task<int> WatchAsync(int pid, double seconds)
{
    using var probe = new ProcessProbe(pid);
    var clock = Stopwatch.StartNew();
    var stats = await WindowAsync(probe, () => clock.Elapsed.TotalSeconds, seconds);
    Console.WriteLine($"pid {pid}: CPU {stats.CpuMs:F0} ms, cycles {stats.CpuCyclesM:F0} M, context switches {stats.ContextSwitchesPerSecond:F1}/s, " +
        $"PWS {stats.First.PrivateWorkingSetMB:F1} -> {stats.Last.PrivateWorkingSetMB:F1} MB, private bytes {stats.First.PrivateBytesMB:F1} -> {stats.Last.PrivateBytesMB:F1} MB " +
        $"(slope {stats.PrivateBytesSlopeMBPerMin:F2} MB/min) over {stats.Seconds:F0} s");
    return 0;
}

static MemoryMapReport MemoryMapOf(int pid)
{
    using var probe = new ProcessProbe(pid);
    return MemoryMap.Capture(probe.Handle);
}

async Task<int> ScenarioAsync(Options options)
{
    var exe = Path.GetFullPath(options.Get("exe"));
    var label = options.Get("label");
    var output = Directory.CreateDirectory(options.Get("out")).FullName;
    var warmup = options.Seconds("warmup", 20);
    var steady = options.Seconds("steady", 60);
    var hidden = options.Seconds("hidden", 20);
    var countersTool = options.Optional("counters") is { } tool ? Path.GetFullPath(tool) : null;
    EnsureNoInstance();

    var clock = Stopwatch.StartNew();
    using var tracks = await TrackLog.StartAsync(clock);
    var launchedAt = clock.Elapsed;
    var startedAt = DateTimeOffset.Now;
    using var process = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false })!;
    try
    {
        using var probe = new ProcessProbe(process.Id);
        double Since() => (clock.Elapsed - launchedAt).TotalSeconds;
        using var watch = WindowWatch.Start(process.Id, Since);
        var startup = await StartupAsync(process, clock, launchedAt);
        var afterTen = await AtAsync(probe, Since, 10);

        await WaitUntilAsync(Since, warmup);
        var background = await WindowAsync(probe, Since, steady);
        var backgroundCounters = countersTool is null ? null : await CountersAsync(countersTool, process.Id);
        var backgroundMap = MemoryMap.Capture(probe.Handle);

        watch.ExpectVisible = true;
        var firstOpen = await OpenAsync(probe, Since);
        await Task.Delay(TimeSpan.FromSeconds(10));
        var openSample = probe.Read(Since());
        var openMap = MemoryMap.Capture(probe.Handle);
        var firstHide = await HideAsync(probe, firstOpen.Window);
        watch.ExpectVisible = false;
        await Task.Delay(TimeSpan.FromSeconds(hidden));
        var hiddenSample = probe.Read(Since());
        var hiddenMap = MemoryMap.Capture(probe.Handle);

        watch.ExpectVisible = true;
        var secondOpen = await OpenAsync(probe, Since);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var secondHide = await HideAsync(probe, secondOpen.Window);
        watch.ExpectVisible = false;
        await Task.Delay(TimeSpan.FromSeconds(10));
        var afterHide = await WindowAsync(probe, Since, steady);
        var afterHideCounters = countersTool is null ? null : await CountersAsync(countersTool, process.Id);
        var afterHideMap = MemoryMap.Capture(probe.Handle);

        var report = new
        {
            Kind = "scenario", Label = label, Exe = exe, ExeSizeMB = new FileInfo(exe).Length / 1048576.0, ExeSha256 = Sha256(exe),
            StartedAt = startedAt, Machine = Environment.MachineName, Cores = Environment.ProcessorCount,
            Phases = new { warmup, steady, hidden },
            Startup = startup, AfterTenSeconds = afterTen,
            Background = background, BackgroundCounters = backgroundCounters, BackgroundMap = backgroundMap,
            FirstOpen = firstOpen.Timing, OpenSample = openSample, OpenMap = openMap,
            FirstHideMs = firstHide, HiddenSample = hiddenSample, HiddenMap = hiddenMap,
            SecondOpen = secondOpen.Timing, SecondHideMs = secondHide,
            AfterHide = afterHide, AfterHideCounters = afterHideCounters, AfterHideMap = afterHideMap,
            Tracks = tracks.Snapshot(), Playback = tracks.Playback(), ExternalWindowOpens = watch.Unexpected()
        };
        var path = Path.Combine(output, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Write(report, path);
        Console.WriteLine($"{label}: ready {startup.InputIdleMs:F0} ms, background PWS {background.Last.PrivateWorkingSetMB:F1} MB, " +
            $"open {firstOpen.Timing.VisibleMs:F0}/{firstOpen.Timing.ResponsiveMs:F0} ms, hidden PWS {hiddenSample.PrivateWorkingSetMB:F1} MB, " +
            $"after-hide PWS {afterHide.Last.PrivateWorkingSetMB:F1} MB, CPU {background.CpuCyclesM:F0}/{afterHide.CpuCyclesM:F0} M cycles per {steady} s, " +
            $"playback {tracks.Playback()}, external opens {watch.Unexpected().Length} -> {path}");
        return 0;
    }
    finally { Stop(process); }
}

async Task<int> SoakAsync(Options options)
{
    var exe = Path.GetFullPath(options.Get("exe"));
    var label = options.Get("label");
    var output = Directory.CreateDirectory(options.Get("out")).FullName;
    var minutes = options.Seconds("minutes", 15);
    var openAt = options.Seconds("open-at", -1);
    var countersTool = options.Optional("counters") is { } tool ? Path.GetFullPath(tool) : null;
    EnsureNoInstance();

    var clock = Stopwatch.StartNew();
    using var tracks = await TrackLog.StartAsync(clock);
    var launchedAt = clock.Elapsed;
    var startedAt = DateTimeOffset.Now;
    using var process = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false })!;
    var name = $"soak-{label}-{DateTime.Now:yyyyMMdd-HHmmss}";
    Process? timeline = null;
    try
    {
        using var probe = new ProcessProbe(process.Id);
        double Since() => (clock.Elapsed - launchedAt).TotalSeconds;
        using var watch = WindowWatch.Start(process.Id, Since);
        var startup = await StartupAsync(process, clock, launchedAt);
        // A 60 s window cannot show a gen0 cycle that takes ~18 min, so the runtime counters are logged
        // for the whole soak.
        var timelinePath = Path.Combine(output, name + "-counters.csv");
        if (countersTool is not null)
        {
            timeline = Process.Start(new ProcessStartInfo(countersTool,
                $"collect -p {process.Id} --counters System.Runtime --refresh-interval 5 --format csv -o \"{timelinePath}\" " +
                $"--duration {TimeSpan.FromSeconds(minutes * 60 - Since()):dd\\:hh\\:mm\\:ss}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            timeline.BeginOutputReadLine();
            timeline.BeginErrorReadLine();
        }
        var samples = new List<Sample>();
        OpenTiming? open = null;
        while (Since() < minutes * 60)
        {
            if (openAt >= 0 && open is null && Since() >= openAt)
            {
                watch.ExpectVisible = true;
                var opened = await OpenAsync(probe, Since);
                await Task.Delay(TimeSpan.FromSeconds(5));
                await HideAsync(probe, opened.Window);
                watch.ExpectVisible = false;
                open = opened.Timing;
            }
            samples.Add(probe.Read(Since()));
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        samples.Add(probe.Read(Since()));
        var counters = countersTool is null ? null : await CountersAsync(countersTool, process.Id);
        var map = MemoryMap.Capture(probe.Handle);
        var report = new
        {
            Kind = "soak", Label = label, Exe = exe, ExeSha256 = Sha256(exe), StartedAt = startedAt, Minutes = minutes, OpenAt = openAt,
            Startup = startup, Open = open, Summary = Summarize(samples), Counters = counters,
            CountersTimeline = countersTool is null ? null : Path.GetFileName(timelinePath), Map = map, Tracks = tracks.Snapshot(),
            Playback = tracks.Playback(), ExternalWindowOpens = watch.Unexpected(), Samples = samples
        };
        var path = Path.Combine(output, name + ".json");
        Write(report, path);
        var summary = Summarize(samples);
        Console.WriteLine($"{label} soak {minutes} min: PWS max {summary.MaxPrivateWorkingSetMB:F1} MB, end {summary.Last.PrivateWorkingSetMB:F1} MB, " +
            $"private bytes max {summary.MaxPrivateBytesMB:F1} MB, slope {summary.PrivateBytesSlopeMBPerMin:F2} MB/min, CPU {summary.CpuMs:F0} ms, " +
            $"tracks {tracks.Snapshot().Length} -> {path}");
        return 0;
    }
    finally
    {
        if (timeline is not null && !timeline.WaitForExit(15_000)) timeline.Kill();
        timeline?.Dispose();
        Stop(process);
    }
}

static void EnsureNoInstance()
{
    var running = Process.GetProcessesByName("AppleMusicPresence");
    if (running.Length > 0)
        throw new InvalidOperationException($"AppleMusicPresence is already running (PID {string.Join(", ", running.Select(p => p.Id))}). " +
            "Stop it first; a second launch would only signal it.");
    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var stale))
    {
        stale.Dispose();
        throw new InvalidOperationException("A show event still exists without a running app.");
    }
}

static void Stop(Process process)
{
    if (process.HasExited) return;
    process.Kill();
    process.WaitForExit(10_000);
}

static async Task<StartupTiming> StartupAsync(Process process, Stopwatch clock, TimeSpan launchedAt)
{
    var showEvent = Task.Run(() =>
    {
        while ((clock.Elapsed - launchedAt).TotalSeconds < 60)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                handle.Dispose();
                return (clock.Elapsed - launchedAt).TotalMilliseconds;
            }
            Thread.Sleep(1);
        }
        throw new TimeoutException("Show event was never created.");
    });
    var inputIdle = Task.Run(() => process.WaitForInputIdle(60_000)
        ? (clock.Elapsed - launchedAt).TotalMilliseconds : throw new TimeoutException("No input idle."));
    return new(await showEvent, await inputIdle);
}

static async Task<Sample> AtAsync(ProcessProbe probe, Func<double> since, double seconds)
{
    await WaitUntilAsync(since, seconds);
    return probe.Read(since());
}

static async Task WaitUntilAsync(Func<double> since, double seconds)
{
    var remaining = seconds - since();
    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining));
}

static async Task<WindowStats> WindowAsync(ProcessProbe probe, Func<double> since, double seconds)
{
    var samples = new List<Sample> { probe.Read(since()) };
    var end = since() + seconds;
    while (since() < end)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        samples.Add(probe.Read(since()));
    }
    return Summarize(samples);
}

static WindowStats Summarize(List<Sample> samples)
{
    var first = samples[0];
    var last = samples[^1];
    var seconds = last.Seconds - first.Seconds;
    double Slope(Func<Sample, double> metric)
    {
        var meanX = samples.Average(s => s.Seconds);
        var meanY = samples.Average(metric);
        var numerator = samples.Sum(s => (s.Seconds - meanX) * (metric(s) - meanY));
        var denominator = samples.Sum(s => (s.Seconds - meanX) * (s.Seconds - meanX));
        return denominator == 0 ? 0 : numerator / denominator * 60;
    }
    return new(first, last, seconds, last.CpuMs - first.CpuMs, last.CpuCyclesM - first.CpuCyclesM,
        last.ContextSwitches - first.ContextSwitches, (last.ContextSwitches - first.ContextSwitches) / seconds,
        (last.PageFaults - first.PageFaults) / seconds,
        samples.Max(s => s.PrivateWorkingSetMB), samples.Max(s => s.PrivateBytesMB), samples.Max(s => s.WorkingSetMB),
        Median(samples.Select(s => s.PrivateWorkingSetMB)), Slope(s => s.PrivateWorkingSetMB), Slope(s => s.PrivateBytesMB), samples);
}

static async Task<(OpenTiming Timing, nint Window)> OpenAsync(ProcessProbe probe, Func<double> since)
{
    using var show = EventWaitHandle.OpenExisting(ShowEventName);
    var before = probe.Read(since());
    var timer = Stopwatch.StartNew();
    show.Set();
    nint window = 0;
    while (window == 0)
    {
        if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Window did not become visible.");
        window = FindVisibleWindow(probe.Pid);
        if (window == 0) Thread.Sleep(1);
    }
    var visible = timer.Elapsed.TotalMilliseconds;
    Native.SendMessageTimeoutW(window, Native.WmNull, 0, 0, Native.SmtoAbortIfHung, 20_000, out _);
    var responsive = timer.Elapsed.TotalMilliseconds;
    var remaining = TimeSpan.FromSeconds(3) - timer.Elapsed;
    if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    var after = probe.Read(since());
    return (new(visible, responsive, after.CpuMs - before.CpuMs, after.CpuCyclesM - before.CpuCyclesM,
        after.PrivateWorkingSetMB - before.PrivateWorkingSetMB), window);
}

static async Task<double> HideAsync(ProcessProbe probe, nint window)
{
    var timer = Stopwatch.StartNew();
    Native.PostMessageW(window, Native.WmClose, 0, 0);
    while (Native.IsWindowVisible(window))
    {
        if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Window did not hide on WM_CLOSE.");
        await Task.Delay(1);
    }
    if (Process.GetProcessById(probe.Pid).HasExited) throw new InvalidOperationException("WM_CLOSE terminated the app.");
    return timer.Elapsed.TotalMilliseconds;
}

static nint FindVisibleWindow(int pid)
{
    nint found = 0;
    var title = new char[256];
    Native.EnumWindows((hwnd, _) =>
    {
        Native.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner != pid || !Native.IsWindowVisible(hwnd)) return true;
        var length = Native.GetWindowTextW(hwnd, title, title.Length);
        if (new string(title, 0, length) != "Apple Music Presence") return true;
        found = hwnd;
        return false;
    }, 0);
    return found;
}

static async Task<Dictionary<string, double>?> CountersAsync(string tool, int pid)
{
    var file = Path.Combine(Path.GetTempPath(), $"amp-counters-{pid}-{Guid.NewGuid():N}.csv");
    try
    {
        using var counters = Process.Start(new ProcessStartInfo(tool,
            $"collect -p {pid} --counters System.Runtime --refresh-interval 1 --format csv -o \"{file}\" --duration 00:00:00:04")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await counters.StandardOutput.ReadToEndAsync();
        await counters.WaitForExitAsync();
        if (!File.Exists(file)) return null;
        var values = new Dictionary<string, double>();
        foreach (var line in File.ReadLines(file).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length >= 5 && double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                values[parts[2]] = value;
        }
        return values;
    }
    finally { if (File.Exists(file)) File.Delete(file); }
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0) return double.NaN;
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

int SummarizeDirectory(string directory)
{
    var runs = Directory.GetFiles(directory, "*.json")
        .Select(path => JsonNode.Parse(File.ReadAllText(path))!.AsObject())
        .Where(run => run["Kind"]?.GetValue<string>() == "scenario")
        .ToArray();
    var interfered = runs.Where(run => run["ExternalWindowOpens"]?.AsArray().Count > 0).ToArray();
    var groups = runs.Except(interfered)
        .GroupBy(run => run["Label"]!.GetValue<string>())
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToArray();
    string Header(IGrouping<string, JsonObject> group) => $"{group.Key} (n={group.Count()}: " + string.Join(", ", group
        .GroupBy(run => run["Playback"]?.GetValue<string>() ?? "?").Select(state => $"{state.Count()} {state.Key}")) + ")";
    (string Name, string Path)[] metrics =
    [
        ("Startup: input idle (ms)", "Startup.InputIdleMs"),
        ("Startup: show event (ms)", "Startup.ShowEventMs"),
        ("Startup CPU, first 10 s (ms)", "AfterTenSeconds.CpuMs"),
        ("Background PWS @ end (MB)", "Background.Last.PrivateWorkingSetMB"),
        ("Background private bytes @ end (MB)", "Background.Last.PrivateBytesMB"),
        ("Background WS @ end (MB)", "Background.Last.WorkingSetMB"),
        ("Background PWS slope (MB/min)", "Background.PrivateWorkingSetSlopeMBPerMin"),
        ("Background private bytes slope (MB/min)", "Background.PrivateBytesSlopeMBPerMin"),
        // Process CPU time advances in 15.6 ms ticks; cycles are the finer measure.
        ("Background CPU per window (ms)", "Background.CpuMs"),
        ("Background CPU cycles per window (M)", "Background.CpuCyclesM"),
        ("Background context switches/s", "Background.ContextSwitchesPerSecond"),
        ("Background page faults/s", "Background.PageFaultsPerSecond"),
        ("Background threads", "Background.Last.Threads"),
        ("Background handles", "Background.Last.Handles"),
        ("GC heap (MB)", "BackgroundCounters.GC Heap Size (MB)"),
        ("GC committed (MB)", "BackgroundCounters.GC Committed Bytes (MB)"),
        ("Allocation rate (B/s)", "BackgroundCounters.Allocation Rate (B / 1 sec)"),
        ("Assemblies loaded", "BackgroundCounters.Number of Assemblies Loaded"),
        ("Methods jitted", "BackgroundCounters.Number of Methods Jitted"),
        ("First open: visible (ms)", "FirstOpen.VisibleMs"),
        ("First open: responsive (ms)", "FirstOpen.ResponsiveMs"),
        ("First open CPU, 3 s (ms)", "FirstOpen.CpuMs"),
        ("First open CPU cycles, 3 s (M)", "FirstOpen.CpuCyclesM"),
        ("Open PWS (MB)", "OpenSample.PrivateWorkingSetMB"),
        ("Hidden PWS after first hide (MB)", "HiddenSample.PrivateWorkingSetMB"),
        ("Second open: visible (ms)", "SecondOpen.VisibleMs"),
        ("Second open: responsive (ms)", "SecondOpen.ResponsiveMs"),
        ("After-hide PWS @ end (MB)", "AfterHide.Last.PrivateWorkingSetMB"),
        ("After-hide private bytes @ end (MB)", "AfterHide.Last.PrivateBytesMB"),
        ("After-hide PWS slope (MB/min)", "AfterHide.PrivateWorkingSetSlopeMBPerMin"),
        ("After-hide CPU per window (ms)", "AfterHide.CpuMs"),
        ("After-hide CPU cycles per window (M)", "AfterHide.CpuCyclesM"),
        ("After-hide context switches/s", "AfterHide.ContextSwitchesPerSecond"),
        ("After-hide page faults/s", "AfterHide.PageFaultsPerSecond"),
        ("Exe size (MB)", "ExeSizeMB"),
    ];
    var text = new StringBuilder();
    text.AppendLine($"| Metric | {string.Join(" | ", groups.Select(Header))} |");
    text.AppendLine($"|---|{string.Concat(groups.Select(_ => "---|"))}");
    foreach (var (name, path) in metrics)
    {
        var cells = groups.Select(group =>
        {
            var values = group.Select(run => Lookup(run, path)).OfType<double>().ToArray();
            return values.Length == 0 ? "n/a" : $"{Median(values):0.##} ({values.Min():0.##}–{values.Max():0.##})";
        });
        text.AppendLine($"| {name} | {string.Join(" | ", cells)} |");
    }
    string[] categories = ["private-pe-copy", "private-data", "private-executable", "section-assembly-copy", "section-other", "image", "mapped-file", "cfg-bitmap"];
    foreach (var map in new[] { "BackgroundMap", "AfterHideMap" })
        foreach (var category in categories)
        {
            var cells = groups.Select(group =>
            {
                double[] Values(string field) => group.Select(run => run[map]?["Categories"]?.AsArray()
                    .FirstOrDefault(c => c?["Name"]?.GetValue<string>() == category)?[field]?.GetValue<double>() ?? 0).ToArray();
                var resident = Values("ResidentMB");
                var privateResident = Values("ResidentPrivateMB");
                return $"{Median(resident):0.#} res / {Median(privateResident):0.#} priv";
            });
            text.AppendLine($"| {map}: {category} (MB) | {string.Join(" | ", cells)} |");
        }
    foreach (var map in new[] { "BackgroundMap", "AfterHideMap" })
    {
        // Runs recorded before maps listed every module have no "Modules" field and are not counted.
        var cells = groups.Select(group =>
        {
            var listed = group.Select(run => run[map]?["Modules"]?.AsArray()).OfType<JsonArray>().ToArray();
            return listed.Length == 0 ? "n/a" : $"{listed.Count(modules => modules.Any(module =>
                string.Equals(module?.GetValue<string>(), "d3d9.dll", StringComparison.OrdinalIgnoreCase)))}/{listed.Length}";
        });
        text.AppendLine($"| {map}: runs with d3d9.dll mapped | {string.Join(" | ", cells)} |");
    }
    var excluded = interfered.Length == 0 ? "" :
        $"\nExcluded {interfered.Length} run(s) where the window was opened outside the harness: " +
        string.Join(", ", interfered.Select(run => run["Label"]!.GetValue<string>())) + "\n";
    Console.Write(text + excluded);
    File.WriteAllText(Path.Combine(directory, "summary.md"), "Median (min–max) per label; the header counts runs by playback state.\n\n" + text + excluded);
    return 0;
}

static double? Lookup(JsonObject run, string path)
{
    JsonNode? node = run;
    foreach (var part in path.Split('.')) node = node is JsonObject parent ? parent[part] : null;
    return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}

internal sealed record StartupTiming(double ShowEventMs, double InputIdleMs);
internal sealed record OpenTiming(double VisibleMs, double ResponsiveMs, double CpuMs, double CpuCyclesM, double PrivateWorkingSetDeltaMB);
internal sealed record WindowStats(
    Sample First, Sample Last, double Seconds, double CpuMs, double CpuCyclesM, long ContextSwitches, double ContextSwitchesPerSecond,
    double PageFaultsPerSecond,
    double MaxPrivateWorkingSetMB, double MaxPrivateBytesMB, double MaxWorkingSetMB, double MedianPrivateWorkingSetMB,
    double PrivateWorkingSetSlopeMBPerMin, double PrivateBytesSlopeMBPerMin, List<Sample> Samples);

internal sealed class Options(string[] args)
{
    public string Get(string name) => Optional(name) ?? throw new ArgumentException($"--{name} is required.");
    public string? Optional(string name)
    {
        var index = Array.IndexOf(args, "--" + name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    public double Seconds(string name, double fallback) =>
        Optional(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>Annotates runs with natural track changes. Read-only GSMTC polling every 5 s.</summary>
internal sealed class TrackLog : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<object> _changes = [];

    private readonly HashSet<string> _states = [];

    public object[] Snapshot() { lock (_changes) return [.. _changes]; }

    public string Playback()
    {
        lock (_changes)
            return _states.Count == 1 ? _states.Single().ToLowerInvariant() : _states.Count == 0 ? "unknown" : "mixed";
    }

    public static async Task<TrackLog> StartAsync(Stopwatch clock)
    {
        var log = new TrackLog();
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _ = Task.Run(async () =>
        {
            string? last = null;
            while (!log._stop.IsCancellationRequested)
            {
                try
                {
                    var session = manager.GetSessions().FirstOrDefault(s =>
                        s.SourceAppUserModelId.Equals("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", StringComparison.OrdinalIgnoreCase));
                    var status = session?.GetPlaybackInfo().PlaybackStatus.ToString() ?? "NoSession";
                    lock (log._changes) log._states.Add(status);
                    var title = session is null ? "(no session)" : (await session.TryGetMediaPropertiesAsync()).Title + " / " + status;
                    if (title != last)
                    {
                        lock (log._changes) log._changes.Add(new { Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), Track = title });
                        last = title;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                try { await Task.Delay(5000, log._stop.Token); } catch (OperationCanceledException) { }
            }
        });
        return log;
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Flags runs where someone opened the app window while the harness expected it hidden.</summary>
internal sealed class WindowWatch : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<double> _unexpected = [];
    public volatile bool ExpectVisible;

    public static WindowWatch Start(int pid, Func<double> since)
    {
        var watch = new WindowWatch();
        _ = Task.Run(async () =>
        {
            var title = new char[256];
            while (!watch._stop.IsCancellationRequested)
            {
                var visible = false;
                Native.EnumWindows((hwnd, _) =>
                {
                    Native.GetWindowThreadProcessId(hwnd, out var owner);
                    if (owner != pid || !Native.IsWindowVisible(hwnd)) return true;
                    var length = Native.GetWindowTextW(hwnd, title, title.Length);
                    visible = new string(title, 0, length) == "Apple Music Presence";
                    return !visible;
                }, 0);
                if (visible && !watch.ExpectVisible) lock (watch._unexpected) watch._unexpected.Add(Math.Round(since(), 1));
                try { await Task.Delay(250, watch._stop.Token); } catch (OperationCanceledException) { }
            }
        });
        return watch;
    }

    public double[] Unexpected() { lock (_unexpected) return [.. _unexpected]; }
    public void Dispose() => _stop.Cancel();
}
