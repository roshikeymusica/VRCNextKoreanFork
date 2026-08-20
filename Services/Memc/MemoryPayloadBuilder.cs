namespace VRCNext.Services.Memc;

// Turns snapshots into the plain objects the frontend renders.
// Every numeric field is paired with a quality code so the UI can never present
// a computed number as if the OS had reported it.
public static class MemoryPayloadBuilder
{
    // dedup != null routes the note through the change-detection cache, so static
    // text is sent once instead of on every push. Exports pass null and get everything.
    private static object Row(string key, string label, long bytes, MemQuality q,
                              string? note = null, long count = -1,
                              MemoryManager? dedup = null, string prefix = "")
        => new
        {
            key,
            label,
            bytes,
            count,
            quality = MemQualityText.Code(q),
            measurable = q != MemQuality.NotMeasurable,
            note = dedup == null ? note : dedup.NoteIfChanged(prefix + "/" + key, note),
        };

    private static object SeriesRow(MemSeriesStats s) => new
    {
        key = s.Key,
        label = s.Label,
        current = s.Current,
        previous = s.Previous,
        start = s.Start,
        min = s.Min,
        max = s.Max,
        avg = s.Average,
        delta = s.Delta,
        growthSinceStart = s.GrowthSinceStart,
        growthPerMinute = s.GrowthPerMinute,
        windowGrowth = s.WindowGrowth,
        windowGrowthPerMinute = s.WindowGrowthPerMinute,
        windowMinutes = Math.Round(s.WindowMinutes, 2),
        sampleCount = s.SampleCount,
        hasGrowthRate = s.HasGrowthRate,
        trend = s.Trend,
    };

    public static object ProcessRows(ProcessMetrics p, MemoryManager? dedup = null) => new[]
    {
        Row("workingSet",  "Working Set",            p.WorkingSet,           MemQuality.Measured, dedup: dedup, prefix: "row"),
        Row("private",     "Private Memory",         p.PrivateMemory,        MemQuality.Measured, "Process commit charge. This is the number Windows counts against the page file.", dedup: dedup, prefix: "row"),
        Row("virtual",     "Virtual Memory",         p.VirtualMemory,        MemQuality.Measured, "Reserved address space, not resident memory.", dedup: dedup, prefix: "row"),
        Row("paged",       "Paged Memory",           p.PagedMemory,          MemQuality.Measured, dedup: dedup, prefix: "row"),
        Row("pagedSystem", "Paged System Memory",    p.PagedSystemMemory,    MemQuality.Measured, "Kernel pool charged to this process.", dedup: dedup, prefix: "row"),
        Row("nonpagedSystem", "Non Paged System Memory", p.NonpagedSystemMemory, MemQuality.Measured, "Kernel non-paged pool charged to this process.", dedup: dedup, prefix: "row"),
        Row("peakWorkingSet", "Peak Working Set",    p.PeakWorkingSet,       MemQuality.Measured, dedup: dedup, prefix: "row"),
        Row("peakVirtual", "Peak Virtual Memory",    p.PeakVirtualMemory,    MemQuality.Measured, dedup: dedup, prefix: "row"),
        Row("peakPaged",   "Peak Paged Memory",      p.PeakPagedMemory,      MemQuality.Measured, dedup: dedup, prefix: "row"),
        Row("handles",     "Handle Count",           0,                      MemQuality.Measured, null, p.HandleCount, dedup: dedup, prefix: "row"),
        Row("threads",     "Thread Count",           0,
            p.ThreadCount >= 0 ? MemQuality.Measured : MemQuality.CountOnly,
            p.ThreadCount >= 0 ? null : "Sampled every 5th tick to keep the profiler cheap.",
            p.ThreadCount, dedup, "row"),
    };

    public static object GcRows(GcMetrics g, long allocRate, MemoryManager? dedup = null) => new[]
    {
        Row("heap",        "GC Heap Size",           g.HeapSize,             g.InfoValid ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, "GCMemoryInfo.HeapSizeBytes, as of the last collection.", dedup: dedup, prefix: "row"),
        Row("managed",     "Total Managed Memory",   g.TotalManagedMemory,   MemQuality.RuntimeCounter, "GC.GetTotalMemory(false).", dedup: dedup, prefix: "row"),
        Row("committed",   "GC Committed Bytes",     g.TotalCommitted,       g.InfoValid ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("reserved",    "GC Reserved Bytes",      0,                      MemQuality.NotMeasurable, "The .NET runtime exposes no reserved-bytes counter. Use Virtual Memory instead.", dedup: dedup, prefix: "row"),
        Row("fragmented",  "GC Fragmentation",       g.Fragmented,           g.InfoValid ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("gen0",        "Gen 0",                  Math.Max(0, g.Gen0Size), g.Gen0Size >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("gen1",        "Gen 1",                  Math.Max(0, g.Gen1Size), g.Gen1Size >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("gen2",        "Gen 2",                  Math.Max(0, g.Gen2Size), g.Gen2Size >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("loh",         "Large Object Heap",      Math.Max(0, g.LohSize),  g.LohSize  >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("poh",         "Pinned Object Heap",     Math.Max(0, g.PohSize),  g.PohSize  >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("gen0frag",    "Gen 0 Fragmentation",    Math.Max(0, g.Gen0Frag), g.Gen0Frag >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("gen2frag",    "Gen 2 Fragmentation",    Math.Max(0, g.Gen2Frag), g.Gen2Frag >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("lohfrag",     "LOH Fragmentation",      Math.Max(0, g.LohFrag),  g.LohFrag  >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("allocated",   "Allocated Bytes (total)", g.TotalAllocatedBytes, MemQuality.RuntimeCounter, "Cumulative since process start. GC.GetTotalAllocatedBytes(false).", dedup: dedup, prefix: "row"),
        Row("allocRate",   "Allocation Rate / s",    Math.Max(0, allocRate), allocRate >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, "Derived from two consecutive samples of the cumulative allocation counter.", dedup: dedup, prefix: "row"),
        Row("gen0col",     "Gen 0 Collections",      0, MemQuality.RuntimeCounter, null, g.Gen0Collections, dedup: dedup, prefix: "row"),
        Row("gen1col",     "Gen 1 Collections",      0, MemQuality.RuntimeCounter, null, g.Gen1Collections, dedup: dedup, prefix: "row"),
        Row("gen2col",     "Gen 2 Collections",      0, MemQuality.RuntimeCounter, null, g.Gen2Collections, dedup: dedup, prefix: "row"),
        Row("pinned",      "Pinned Objects",         0, g.PinnedObjectsCount >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, null, g.PinnedObjectsCount, dedup: dedup, prefix: "row"),
        Row("finalizers",  "Finalization Queue",     0, g.FinalizationPendingCount >= 0 ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, "GCMemoryInfo.FinalizationPendingCount.", g.FinalizationPendingCount, dedup: dedup, prefix: "row"),
        Row("gcHandles",   "GC Handles",             0, MemQuality.NotMeasurable, "No public .NET API exposes the GC handle count.", dedup: dedup, prefix: "row"),
        Row("memoryLoad",  "Machine Memory Load",    g.MemoryLoadBytes,      g.InfoValid ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
        Row("available",   "Machine Memory Total",   g.TotalAvailableMemory, g.InfoValid ? MemQuality.RuntimeCounter : MemQuality.NotMeasurable, dedup: dedup, prefix: "row"),
    };

    // The attribution model. Managed instrumented bytes live INSIDE the GC heap, so
    // they are never added to it. Native probes live outside, so they are subtracted
    // from the non-GC part of the process commit.
    public static object Attribution(MemoryManager mgr, MemorySnapshot s)
    {
        var managedAttr = MemoryManager.ManagedAttributed(s);
        var nativeAttr  = MemoryManager.NativeAttributed(s);
        var gcCommitted = s.Gc.TotalCommitted;
        var gcHeap      = s.Gc.HeapSize;
        var priv        = s.Process.PrivateMemory;
        var nonGc       = Math.Max(0, priv - gcCommitted);
        return new
        {
            processPrivate      = priv,
            gcCommitted,
            gcHeap,
            nonGcCommit         = nonGc,
            managedAttributed   = managedAttr,
            managedUnattributed = Math.Max(0, gcHeap - managedAttr),
            nativeAttributed    = nativeAttr,
            nativeUnattributed  = Math.Max(0, nonGc - nativeAttr),
            totalAttributed     = managedAttr + nativeAttr,
            totalUnattributed   = Math.Max(0, gcHeap - managedAttr) + Math.Max(0, nonGc - nativeAttr),
            formula = new
            {
                managed = "GC heap - sum(instrumented module bytes) = managed memory we cannot attribute",
                native  = "Process private - GC committed - sum(native probes) = native memory we cannot attribute",
            },
        };
    }

    // Series the Overview always needs. Everything else is only sent when a view
    // actually renders it (Growth tab), because the full list is ~45 KB per push.
    private static bool IsHeadlineSeries(string key)
        => key.StartsWith("proc.", StringComparison.Ordinal)
        || key.StartsWith("gc.", StringComparison.Ordinal)
        || key.StartsWith("attr.", StringComparison.Ordinal);

    public static object BuildLive(MemoryManager mgr, MemorySnapshot s)
    {
        var seriesStats = new List<object>();
        foreach (var ser in mgr.AllSeries)
            if (mgr.WantAllSeries || IsHeadlineSeries(ser.Key))
                seriesStats.Add(SeriesRow(ser.Stats()));

        var sendLegend = mgr.TakeLegendSlot();

        return new
        {
            enabled = mgr.Enabled,
            intervalMs = mgr.IntervalMs,
            sampleCount = mgr.SampleCount,
            takenAtUtc = s.TakenAtUtc.ToString("o"),
            processUptimeMs = s.ProcessUptimeMs,
            consoleUptimeMs = (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - mgr.EnabledAtTicks).TotalMilliseconds,
            allSeries = mgr.WantAllSeries,
            process = ProcessRows(s.Process, mgr),
            gc = GcRows(s.Gc, mgr.AllocRatePerSecond(), mgr),
            gcFlags = new
            {
                serverGc = s.Gc.ServerGc,
                lastGcGeneration = s.Gc.LastGcGeneration,
                lastGcIndex = s.Gc.LastGcIndex,
                lastGcCompacted = s.Gc.LastGcCompacted,
                lastGcConcurrent = s.Gc.LastGcConcurrent,
                pauseTimePercentage = Math.Round(s.Gc.PauseTimePercentage, 3),
            },
            attribution = Attribution(mgr, s),
            modules = s.Modules.Select(m => new
            {
                id = m.Id,
                label = m.Label,
                active = m.Active,
                everActive = m.EverActive,
                attributedBytes = m.AttributedBytes,
                informationalBytes = m.InformationalBytes,
                throughputBytes = m.ThroughputBytes,
                lifecycleNote = m.LifecycleNote,
                resources = m.Resources.Select(r => new
                {
                    id = r.Id,
                    label = r.Label,
                    category = r.Category,
                    bytes = r.Bytes,
                    count = r.Count,
                    quality = r.Quality,
                    attributed = r.Attributed,
                    // Static text is sent once and cached by the frontend.
                    note = mgr.NoteIfChanged(m.Id + "/" + r.Id, r.Note),
                    contendedReads = r.ContendedReads,
                }),
            }),
            series = seriesStats,
            snapshots = new
            {
                a = SnapSummary(mgr.SnapshotA),
                b = SnapSummary(mgr.SnapshotB),
                baseline = SnapSummary(mgr.Baseline),
            },
            gcCompare = GcCompare(mgr.LastGcCompare),
            profiler = new
            {
                selfAllocPerSample = mgr.SelfAllocPerSample,
                historyBytes = mgr.HistoryBytes(),
                seriesCount = mgr.AllSeries.Count(),
                sampleDurationMs = Math.Round(
                    s.SampleDurationTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3),
                threadName = "VRCNext-Memc",
            },
            legend = sendLegend
                ? Enum.GetValues<MemQuality>().Select(q => new
                  {
                      code = MemQualityText.Code(q),
                      description = MemQualityText.Describe(q),
                  })
                : null,
        };
    }


    private static object? SnapSummary(MemorySnapshot? s) => s == null ? null : new
    {
        name = s.Name,
        takenAtUtc = s.TakenAtUtc.ToString("o"),
        workingSet = s.Process.WorkingSet,
        privateMemory = s.Process.PrivateMemory,
        gcHeap = s.Gc.HeapSize,
        gcCommitted = s.Gc.TotalCommitted,
        attributed = s.TotalAttributedBytes,
    };

    private static object? GcCompare(GcCompareResult? r)
    {
        if (r == null) return null;
        var b = r.Before; var a = r.After;
        var reclaimed = b.Gc.HeapSize - a.Gc.HeapSize;
        var stillHeld = a.Gc.HeapSize;
        var nativeBefore = Math.Max(0, b.Process.PrivateMemory - b.Gc.TotalCommitted);
        var nativeAfter = Math.Max(0, a.Process.PrivateMemory - a.Gc.TotalCommitted);
        return new
        {
            atUtc = r.AtUtc.ToString("o"),
            before = new { workingSet = b.Process.WorkingSet, privateMemory = b.Process.PrivateMemory, gcHeap = b.Gc.HeapSize, gcCommitted = b.Gc.TotalCommitted, fragmented = b.Gc.Fragmented, finalizers = b.Gc.FinalizationPendingCount },
            after  = new { workingSet = a.Process.WorkingSet, privateMemory = a.Process.PrivateMemory, gcHeap = a.Gc.HeapSize, gcCommitted = a.Gc.TotalCommitted, fragmented = a.Gc.Fragmented, finalizers = a.Gc.FinalizationPendingCount },
            derived = new[]
            {
                new { label = "Reclaimable managed memory", bytes = reclaimed, formula = "GC heap before - GC heap after" },
                new { label = "Still referenced managed memory", bytes = stillHeld, formula = "GC heap after a full blocking compacting collection" },
                new { label = "Native memory (outside the GC)", bytes = nativeAfter, formula = "Process private - GC committed, after collection" },
                new { label = "Native change across the collection", bytes = nativeAfter - nativeBefore, formula = "native after - native before" },
                new { label = "Working set not yet returned by Windows", bytes = a.Process.WorkingSet - a.Gc.TotalCommitted, formula = "Working set after - GC committed after" },
            },
        };
    }

    // A/B comparison

    public static object BuildCompare(MemoryManager mgr)
    {
        var a = mgr.SnapshotA;
        var b = mgr.SnapshotB;
        if (a == null || b == null)
            return new { ok = false, reason = a == null && b == null ? "Capture A and B first." : a == null ? "Capture A first." : "Capture B first." };

        var rows = new List<object>
        {
            Diff("Working Set",           a.Process.WorkingSet,      b.Process.WorkingSet,      "measured"),
            Diff("Private Memory",        a.Process.PrivateMemory,   b.Process.PrivateMemory,   "measured"),
            Diff("Virtual Memory",        a.Process.VirtualMemory,   b.Process.VirtualMemory,   "measured"),
            Diff("Paged Memory",          a.Process.PagedMemory,     b.Process.PagedMemory,     "measured"),
            Diff("Peak Working Set",      a.Process.PeakWorkingSet,  b.Process.PeakWorkingSet,  "measured"),
            Diff("GC Heap",               a.Gc.HeapSize,             b.Gc.HeapSize,             "runtime"),
            Diff("GC Committed",          a.Gc.TotalCommitted,       b.Gc.TotalCommitted,       "runtime"),
            Diff("GC Fragmentation",      a.Gc.Fragmented,           b.Gc.Fragmented,           "runtime"),
            Diff("Gen 0",                 Math.Max(0, a.Gc.Gen0Size), Math.Max(0, b.Gc.Gen0Size), "runtime"),
            Diff("Gen 1",                 Math.Max(0, a.Gc.Gen1Size), Math.Max(0, b.Gc.Gen1Size), "runtime"),
            Diff("Gen 2",                 Math.Max(0, a.Gc.Gen2Size), Math.Max(0, b.Gc.Gen2Size), "runtime"),
            Diff("Large Object Heap",     Math.Max(0, a.Gc.LohSize),  Math.Max(0, b.Gc.LohSize),  "runtime"),
            Diff("Pinned Object Heap",    Math.Max(0, a.Gc.PohSize),  Math.Max(0, b.Gc.PohSize),  "runtime"),
            Diff("Allocated Bytes total", a.Gc.TotalAllocatedBytes,  b.Gc.TotalAllocatedBytes,  "runtime"),
        };

        // Per-module and per-resource deltas, keyed so modules present in only one snapshot still show up.
        var modA = a.Modules.ToDictionary(m => m.Id);
        var modB = b.Modules.ToDictionary(m => m.Id);
        var moduleRows = new List<ModuleDiffRow>();
        foreach (var id in modA.Keys.Union(modB.Keys))
        {
            modA.TryGetValue(id, out var ma);
            modB.TryGetValue(id, out var mb);
            var resA = ma?.Resources.ToDictionary(r => r.Id) ?? new Dictionary<string, ModuleResourceSnapshot>();
            var resB = mb?.Resources.ToDictionary(r => r.Id) ?? new Dictionary<string, ModuleResourceSnapshot>();
            var resRows = new List<object>();
            foreach (var rid in resA.Keys.Union(resB.Keys))
            {
                resA.TryGetValue(rid, out var ra);
                resB.TryGetValue(rid, out var rb);
                var label = rb?.Label ?? ra?.Label ?? rid;
                var quality = rb?.Quality ?? ra?.Quality ?? "unmeasurable";
                var av = ra?.Bytes ?? 0;
                var bv = rb?.Bytes ?? 0;
                if (av == 0 && bv == 0) continue;
                resRows.Add(new
                {
                    label,
                    category = rb?.Category ?? ra?.Category ?? "",
                    quality,
                    attributed = rb?.Attributed ?? ra?.Attributed ?? false,
                    a = av, b = bv, delta = bv - av,
                    countA = ra?.Count ?? -1, countB = rb?.Count ?? -1,
                });
            }
            moduleRows.Add(new ModuleDiffRow
            {
                id = id,
                label = mb?.Label ?? ma?.Label ?? id,
                activeA = ma?.Active ?? false,
                activeB = mb?.Active ?? false,
                a = ma?.AttributedBytes ?? 0,
                b = mb?.AttributedBytes ?? 0,
                delta = (mb?.AttributedBytes ?? 0) - (ma?.AttributedBytes ?? 0),
                lifecycleNote = mb?.LifecycleNote,
                resources = resRows,
            });
        }

        var managedA = MemoryManager.ManagedAttributed(a);
        var managedB = MemoryManager.ManagedAttributed(b);
        var nativeA  = MemoryManager.NativeAttributed(a);
        var nativeB  = MemoryManager.NativeAttributed(b);
        var privDelta = b.Process.PrivateMemory - a.Process.PrivateMemory;
        var explained = (managedB - managedA) + (nativeB - nativeA);
        var gcOverheadDelta = (b.Gc.TotalCommitted - b.Gc.HeapSize) - (a.Gc.TotalCommitted - a.Gc.HeapSize);
        var unattributed = privDelta - explained - gcOverheadDelta;

        return new
        {
            ok = true,
            a = new { takenAtUtc = a.TakenAtUtc.ToString("o"), name = a.Name },
            b = new { takenAtUtc = b.TakenAtUtc.ToString("o"), name = b.Name },
            elapsedMs = (long)(b.TakenAtUtc - a.TakenAtUtc).TotalMilliseconds,
            rows,
            modules = moduleRows.OrderByDescending(m => Math.Abs(m.delta)),
            attribution = new
            {
                processPrivateDelta = privDelta,
                managedAttributedDelta = managedB - managedA,
                nativeAttributedDelta = nativeB - nativeA,
                gcOverheadDelta,
                explainedDelta = explained + gcOverheadDelta,
                unattributedDelta = unattributed,
                formula = "unattributed = private delta - instrumented managed delta - native probe delta - GC overhead delta",
            },
        };
    }

    private static object Diff(string label, long av, long bv, string quality)
        => new { label, a = av, b = bv, delta = bv - av, quality };
}

// Serialized straight to JSON, so the lowercase member names match the frontend.
public sealed class ModuleDiffRow
{
    public string id { get; set; } = "";
    public string label { get; set; } = "";
    public bool activeA { get; set; }
    public bool activeB { get; set; }
    public long a { get; set; }
    public long b { get; set; }
    public long delta { get; set; }
    public string? lifecycleNote { get; set; }
    public List<object> resources { get; set; } = new();
}
