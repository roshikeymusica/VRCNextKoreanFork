using System.Collections.Concurrent;

namespace VRCNext.Services.Memc;

// The Memory Console.
//
// Hard rule while disabled: no thread, no timer, no registrations, no history,
// no delegates held. SetEnabled(false) tears all of it down again.
public sealed class MemoryManager : IDisposable
{
    public const int DefaultIntervalMs = 2000;

    private readonly object _lock = new();
    private Thread? _sampler;
    private volatile bool _running;
    private volatile bool _enabled;
    private ManualResetEventSlim? _wake;

    private MemoryModuleTracker? _modules;
    private ConcurrentDictionary<string, MemSeries>? _series;
    private MemorySnapshot? _baseline;
    private MemorySnapshot? _snapA;
    private MemorySnapshot? _snapB;
    private MemorySnapshot? _latest;
    private GcCompareResult? _lastGcCompare;

    private long _sampleCount;
    private long _selfAllocPerSample;
    private long _enabledAtTicks;
    private int _intervalMs = DefaultIntervalMs;

    public bool Enabled => _enabled;
    public bool ViewOpen { get; set; }

    // Live-payload slimming.
    //
    // The payload is pushed twice a second while the window is open, so anything
    // static in it is re-sent hundreds of times. Measured at 156 KB per push, the
    // Memory Console was producing 58% of all WebView bridge traffic - it was the
    // biggest single contributor to the very growth it is meant to measure.
    // Static text is therefore sent once and cached by the frontend, and the full
    // series list only goes out while the Growth tab needs it.
    private readonly ConcurrentDictionary<string, string> _sentNotes = new();
    private int _legendSent;
    /// <summary>Set by the frontend when a view needs every series, not just the headline ones.</summary>
    public bool WantAllSeries { get; set; }

    /// <summary>Forces the next payload to carry all static text again.</summary>
    public void ResetPayloadCache()
    {
        _sentNotes.Clear();
        Interlocked.Exchange(ref _legendSent, 0);
    }

    /// <summary>Returns the note only when it changed since it was last sent.</summary>
    internal string? NoteIfChanged(string key, string? note)
    {
        if (note == null)
        {
            _sentNotes.TryRemove(key, out _);
            return null;
        }
        if (_sentNotes.TryGetValue(key, out var prev) && prev == note) return null;
        _sentNotes[key] = note;
        return note;
    }

    internal bool TakeLegendSlot() => Interlocked.Exchange(ref _legendSent, 1) == 0;
    public int IntervalMs => _intervalMs;
    public long SampleCount => Interlocked.Read(ref _sampleCount);
    public MemoryModuleTracker? Modules => _modules;
    public long EnabledAtTicks => _enabledAtTicks;

    /// <summary>Called once when the console is enabled so modules can register their resources.</summary>
    public Action<MemoryModuleTracker>? Registrar { get; set; }
    /// <summary>Pushes a message to the frontend. Set by AppShell.</summary>
    public Action<string, object?>? Send { get; set; }
    /// <summary>Writes a line into the Activity Log.</summary>
    public Action<string, string>? Log { get; set; }
    /// <summary>Reports which VRCNext tools are currently running, for the export header.</summary>
    public Func<Dictionary<string, bool>>? ActiveToolsProvider { get; set; }

    public bool SetEnabled(bool on, int? intervalMs = null)
    {
        lock (_lock)
        {
            if (intervalMs is int ms) _intervalMs = Math.Clamp(ms, 250, 60_000);
            if (on == _enabled) return false;

            if (on)
            {
                _modules = new MemoryModuleTracker();
                _series = new ConcurrentDictionary<string, MemSeries>();
                _sampleCount = 0;
                _snapA = null; _snapB = null; _latest = null;
                _lastGcCompare = null;
                _enabledAtTicks = DateTime.UtcNow.Ticks;
                try { Registrar?.Invoke(_modules); }
                catch (Exception ex) { CrashHandler.WriteEntry("MemoryManager.Registrar", ex); }
                _enabled = true;
                _baseline = BuildSnapshot("baseline", deep: false);
                _wake = new ManualResetEventSlim(false);
                _running = true;
                _sampler = new Thread(SamplerLoop)
                {
                    IsBackground = true,
                    Name = "VRCNext-Memc",
                    Priority = ThreadPriority.BelowNormal,
                };
                _sampler.Start();
            }
            else
            {
                _enabled = false;
                _running = false;
                _wake?.Set();
                try { _sampler?.Join(1500); } catch { }
                _sampler = null;
                _wake?.Dispose();
                _wake = null;
                _modules?.Clear();
                _modules = null;
                _series = null;
                _baseline = null; _snapA = null; _snapB = null; _latest = null;
                _lastGcCompare = null;
                ViewOpen = false;
                WantAllSeries = false;
                ResetPayloadCache();
                _sampleCount = 0;
            }
            return true;
        }
    }

    private void SamplerLoop()
    {
        while (_running)
        {
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var snap = BuildSnapshot("live", deep: false);
                RecordSeries(snap);
                _latest = snap;
                Interlocked.Increment(ref _sampleCount);
                _selfAllocPerSample = GC.GetAllocatedBytesForCurrentThread() - before;

                if (ViewOpen && Send != null)
                {
                    try { Send("memcLive", BuildLivePayload()); }
                    catch (Exception ex) { CrashHandler.WriteEntry("MemoryManager.push", ex); }
                }
            }
            catch (Exception ex) { CrashHandler.WriteEntry("MemoryManager.SamplerLoop", ex); }

            try
            {
                var w = _wake;
                if (w == null) break;
                w.Wait(_intervalMs);
                w.Reset();
            }
            catch { break; }
        }
    }

    public void RequestImmediateSample() => _wake?.Set();

    // Snapshot construction

    public MemorySnapshot BuildSnapshot(string name, bool deep)
    {
        var sw = System.Diagnostics.Stopwatch.GetTimestamp();
        var nowUtc = DateTime.UtcNow;
        // Thread count enumerates a ProcessThreadCollection, so it is only read on
        // every 5th live sample and always for explicit snapshots.
        var heavy = deep || name != "live" || (Interlocked.Read(ref _sampleCount) % 5 == 0);

        var snap = new MemorySnapshot
        {
            Name = name,
            TakenAtUtc = nowUtc,
            Process = MemoryProcessReader.Read(heavy),
            Gc = GcMetrics.Read(),
        };
        if (snap.Process.Valid)
            snap.ProcessUptimeMs = (long)(nowUtc - snap.Process.StartTimeUtc).TotalMilliseconds;

        snap.Modules = _modules?.Snapshot(deep, nowUtc) ?? new List<ModuleSnapshot>();

        long attributed = 0;
        foreach (var m in snap.Modules) attributed += m.AttributedBytes;
        snap.TotalAttributedBytes = attributed;
        snap.UnattributedBytes = Math.Max(0,
            snap.Process.PrivateMemory - snap.Gc.TotalCommitted - NativeAttributed(snap));
        snap.ProfilerSelfAllocBytes = _selfAllocPerSample;
        snap.SampleDurationTicks = System.Diagnostics.Stopwatch.GetTimestamp() - sw;
        return snap;
    }

    internal static long NativeAttributed(MemorySnapshot s)
    {
        long n = 0;
        foreach (var m in s.Modules)
            foreach (var r in m.Resources)
                if (r.Attributed && r.Quality == "probe") n += r.Bytes;
        return n;
    }

    internal static long ManagedAttributed(MemorySnapshot s)
    {
        long n = 0;
        foreach (var m in s.Modules)
            foreach (var r in m.Resources)
                if (r.Attributed && r.Quality == "instrumented") n += r.Bytes;
        return n;
    }

    private MemSeries Series(string key, string label)
        => _series!.GetOrAdd(key, _ => new MemSeries(key, label));

    private void RecordSeries(MemorySnapshot s)
    {
        if (_series == null) return;
        var t = s.TakenAtUtc.Ticks;
        Series("proc.workingSet", "Working Set").Add(s.Process.WorkingSet, t);
        Series("proc.private", "Private Memory").Add(s.Process.PrivateMemory, t);
        Series("proc.virtual", "Virtual Memory").Add(s.Process.VirtualMemory, t);
        Series("proc.paged", "Paged Memory").Add(s.Process.PagedMemory, t);
        Series("proc.handles", "Handle Count").Add(s.Process.HandleCount, t);
        if (s.Process.ThreadCount >= 0) Series("proc.threads", "Thread Count").Add(s.Process.ThreadCount, t);
        Series("gc.heap", "GC Heap Size").Add(s.Gc.HeapSize, t);
        Series("gc.managed", "Total Managed Memory").Add(s.Gc.TotalManagedMemory, t);
        Series("gc.committed", "GC Committed").Add(s.Gc.TotalCommitted, t);
        Series("gc.fragmented", "GC Fragmentation").Add(s.Gc.Fragmented, t);
        Series("gc.allocated", "Allocated Bytes (total)").Add(s.Gc.TotalAllocatedBytes, t);
        if (s.Gc.Gen0Size >= 0) Series("gc.gen0", "Gen 0").Add(s.Gc.Gen0Size, t);
        if (s.Gc.Gen1Size >= 0) Series("gc.gen1", "Gen 1").Add(s.Gc.Gen1Size, t);
        if (s.Gc.Gen2Size >= 0) Series("gc.gen2", "Gen 2").Add(s.Gc.Gen2Size, t);
        if (s.Gc.LohSize >= 0) Series("gc.loh", "Large Object Heap").Add(s.Gc.LohSize, t);
        if (s.Gc.PohSize >= 0) Series("gc.poh", "Pinned Object Heap").Add(s.Gc.PohSize, t);
        Series("attr.total", "Attributed (instrumented + probe)").Add(s.TotalAttributedBytes, t);
        Series("attr.unattributed", "Unattributed native").Add(s.UnattributedBytes, t);
        foreach (var m in s.Modules)
        {
            Series("mod." + m.Id, m.Label).Add(m.AttributedBytes, t);
            foreach (var r in m.Resources)
                // Throughput rows are cumulative counters, so their series slope is
                // literally bytes-per-minute over the bridge. Worth tracking even though
                // they are not attributed memory.
                if (r.Attributed || r.Quality == "throughput")
                    Series("res." + m.Id + "." + r.Id, m.Label + " / " + r.Label).Add(r.Bytes, t);
        }
    }

    // Allocation rate, derived from two consecutive samples of the cumulative counter.
    internal long AllocRatePerSecond()
    {
        var s = _series?.GetValueOrDefault("gc.allocated");
        if (s == null) return -1;
        var st = s.Stats();
        if (st.SampleCount < 2 || _intervalMs <= 0) return -1;
        return st.Delta * 1000L / _intervalMs;
    }

    // Public operations

    public void Capture(string slot)
    {
        if (!_enabled) return;
        var isA = string.Equals(slot, "A", StringComparison.OrdinalIgnoreCase);
        var snap = BuildSnapshot(isA ? "A" : "B", deep: true);
        if (isA) _snapA = snap; else _snapB = snap;
        Log?.Invoke($"[MEMC] Snapshot {(isA ? "A" : "B")} captured — "
                  + $"WS {MemorySizer.Human(snap.Process.WorkingSet)}, "
                  + $"private {MemorySizer.Human(snap.Process.PrivateMemory)}, "
                  + $"GC heap {MemorySizer.Human(snap.Gc.HeapSize)}", "sec");
    }

    public void DeepMeasure()
    {
        if (!_enabled) return;
        var snap = BuildSnapshot("deep", deep: true);
        _latest = snap;
        RecordSeries(snap);
        Log?.Invoke($"[MEMC] Deep measure complete — attributed {MemorySizer.Human(snap.TotalAttributedBytes)}", "sec");
    }

    public GcCompareResult ForceGc()
    {
        var before = BuildSnapshot("beforeGC", deep: false);
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = BuildSnapshot("afterGC", deep: false);
        var r = new GcCompareResult { Before = before, After = after, AtUtc = DateTime.UtcNow };
        _lastGcCompare = r;
        Log?.Invoke($"[MEMC] Forced GC — managed heap {MemorySizer.Human(before.Gc.HeapSize)} -> "
                  + $"{MemorySizer.Human(after.Gc.HeapSize)}, working set "
                  + $"{MemorySizer.Human(before.Process.WorkingSet)} -> {MemorySizer.Human(after.Process.WorkingSet)}", "sec");
        return r;
    }

    public MemorySnapshot? Latest => _latest;
    public MemorySnapshot? Baseline => _baseline;
    public MemorySnapshot? SnapshotA => _snapA;
    public MemorySnapshot? SnapshotB => _snapB;
    public GcCompareResult? LastGcCompare => _lastGcCompare;
    public IEnumerable<MemSeries> AllSeries => _series?.Values ?? Enumerable.Empty<MemSeries>();
    public MemSeries? SeriesFor(string key) => _series?.GetValueOrDefault(key);
    public long SelfAllocPerSample => _selfAllocPerSample;

    public long HistoryBytes()
    {
        long total = 0;
        foreach (var s in AllSeries) total += s.BytesOfSelf;
        return total;
    }

    public object BuildLivePayload()
    {
        var snap = _latest ?? BuildSnapshot("live", deep: false);
        return MemoryPayloadBuilder.BuildLive(this, snap);
    }

    public object BuildComparePayload() => MemoryPayloadBuilder.BuildCompare(this);

    public string StatusText()
    {
        if (!_enabled)
            return "Memory Console: OFF\n"
                 + "  No sampler thread, no timers, no history, no module registrations.\n"
                 + "  Enable with: /memc true";
        var up = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _enabledAtTicks);
        var snap = _latest;
        var text = "Memory Console: ON\n"
             + $"  Sampling every {_intervalMs} ms on a dedicated background thread.\n"
             + $"  Samples: {SampleCount}   Active for: {up:hh\\:mm\\:ss}\n"
             + $"  Modules registered: {_modules?.Count ?? 0}   Series: {_series?.Count ?? 0}\n"
             + $"  History footprint: {MemorySizer.Human(HistoryBytes())}\n"
             + $"  Profiler allocates {MemorySizer.Human(_selfAllocPerSample)} per sample.";
        if (snap != null)
            text += $"\n  Working set {MemorySizer.Human(snap.Process.WorkingSet)}, "
                  + $"private {MemorySizer.Human(snap.Process.PrivateMemory)}, "
                  + $"GC heap {MemorySizer.Human(snap.Gc.HeapSize)}.";
        return text;
    }

    public void Dispose() => SetEnabled(false);
}

public sealed class GcCompareResult
{
    public DateTime AtUtc;
    public MemorySnapshot Before = new();
    public MemorySnapshot After = new();
}
