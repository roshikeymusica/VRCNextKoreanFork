using System.Collections.Concurrent;

namespace VRCNext.Services.Memc;

// One VRCNext component (service, tool or controller) with the resources it owns.
public sealed class MemModule
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public Func<bool>? IsActive { get; set; }

    /// <summary>
    /// Runs immediately before this module is sampled. Lets a module whose resource
    /// set is discovered at runtime (e.g. one row per observed message type) keep its
    /// rows in sync. Add is idempotent by id, so re-adding is free.
    /// </summary>
    public Action? OnBeforeSnapshot { get; set; }

    private readonly List<MemResource> _resources = new();
    private readonly ConcurrentDictionary<string, MemNativeProbe> _probes = new();
    private readonly object _lock = new();

    public bool EverActive { get; private set; }
    public bool LastActiveState { get; private set; }
    public DateTime LastStartUtc { get; private set; }
    public DateTime LastStopUtc { get; private set; }
    public long BytesAtStop { get; private set; } = -1;
    public long RetainedAfterStopBytes { get; private set; } = -1;
    public long PeakActiveBytes { get; private set; } = -1;
    internal bool PendingStopCheck { get; private set; }

    // Last value observed while the module was still running. This is what
    // BytesAtStop must use, because by the time a sample sees active == false
    // the tool has already torn itself down.
    private long _lastActiveBytes = -1;

    // Idempotent by Id so a module can re-register when its backing service is
    // recreated (tool restart) without accumulating duplicate rows.
    public void Add(MemResource r)
    {
        lock (_lock)
        {
            var idx = _resources.FindIndex(x => x.Id == r.Id);
            if (idx >= 0) _resources[idx] = r;
            else _resources.Add(r);
        }
    }

    public void RemoveResource(string id)
    {
        lock (_lock) _resources.RemoveAll(x => x.Id == id);
    }

    public IReadOnlyList<MemResource> Resources { get { lock (_lock) return _resources.ToArray(); } }

    public MemNativeProbe Probe(string id, string label, string category)
        => _probes.GetOrAdd(id, _ => new MemNativeProbe { Id = id, Label = label, Category = category });

    public IEnumerable<MemNativeProbe> Probes => _probes.Values;

    public bool ReadActive()
    {
        try { return IsActive?.Invoke() ?? false; } catch { return false; }
    }

    // Called once per sample. Detects start/stop transitions so the UI can answer
    // "did this module actually give its memory back when it was shut down?".
    internal void ObserveLifecycle(bool activeNow, long attributedNow, DateTime nowUtc)
    {
        if (activeNow && !LastActiveState)
        {
            LastStartUtc = nowUtc;
            EverActive = true;
            RetainedAfterStopBytes = -1;
            BytesAtStop = -1;
            PeakActiveBytes = attributedNow;
            PendingStopCheck = false;
        }
        else if (!activeNow && LastActiveState)
        {
            LastStopUtc = nowUtc;
            // Use the last value seen while the module was still running, not the
            // value after it already released. Falls back to now if we never saw it running.
            BytesAtStop = _lastActiveBytes >= 0 ? _lastActiveBytes : attributedNow;
            PendingStopCheck = true;
        }
        else if (PendingStopCheck && !activeNow && (nowUtc - LastStopUtc).TotalSeconds >= 5)
        {
            RetainedAfterStopBytes = attributedNow;
            PendingStopCheck = false;
        }

        if (activeNow)
        {
            _lastActiveBytes = attributedNow;
            if (attributedNow > PeakActiveBytes) PeakActiveBytes = attributedNow;
        }
        LastActiveState = activeNow;
    }

    public string? BuildLifecycleNote()
    {
        if (!EverActive) return null;
        if (LastActiveState)
            return $"Running since {LastStartUtc:HH:mm:ss} UTC."
                 + (PeakActiveBytes > 0 ? $" Peak while running: {MemorySizer.Human(PeakActiveBytes)}." : "");
        if (RetainedAfterStopBytes >= 0)
        {
            var released = BytesAtStop - RetainedAfterStopBytes;
            var verdict = RetainedAfterStopBytes <= 0
                ? "Everything tracked was released."
                : BytesAtStop > 0 && RetainedAfterStopBytes >= BytesAtStop
                    ? "NOTHING was released — this memory is still held after shutdown."
                    : $"{MemorySizer.Human(RetainedAfterStopBytes)} is still held after shutdown.";
            return $"Stopped {LastStopUtc:HH:mm:ss} UTC. Held {MemorySizer.Human(BytesAtStop)} while running, "
                 + $"released {MemorySizer.Human(released)}. {verdict}";
        }
        if (PendingStopCheck)
            return $"Stopped {LastStopUtc:HH:mm:ss} UTC (held {MemorySizer.Human(BytesAtStop)} while running). "
                 + "Measuring what was released...";
        return $"Stopped {LastStopUtc:HH:mm:ss} UTC.";
    }
}

public sealed class MemoryModuleTracker
{
    private readonly ConcurrentDictionary<string, MemModule> _modules = new();
    private readonly List<string> _order = new();
    private readonly object _orderLock = new();

    public MemModule Module(string id, string label)
    {
        var created = false;
        var m = _modules.GetOrAdd(id, _ => { created = true; return new MemModule { Id = id, Label = label }; });
        if (created) { lock (_orderLock) _order.Add(id); }
        return m;
    }

    public MemModule? Find(string id) => _modules.TryGetValue(id, out var m) ? m : null;

    public void Clear()
    {
        _modules.Clear();
        lock (_orderLock) _order.Clear();
    }

    public int Count => _modules.Count;

    public List<ModuleSnapshot> Snapshot(bool includeDeep, DateTime nowUtc)
    {
        string[] ids;
        lock (_orderLock) ids = _order.ToArray();

        var result = new List<ModuleSnapshot>(ids.Length);
        foreach (var id in ids)
        {
            if (!_modules.TryGetValue(id, out var mod)) continue;
            try { mod.OnBeforeSnapshot?.Invoke(); } catch { }
            var snap = new ModuleSnapshot { Id = mod.Id, Label = mod.Label, Active = mod.ReadActive() };

            long attributed = 0, informational = 0, throughput = 0;
            foreach (var res in mod.Resources)
            {
                var metric = res.Read(includeDeep);
                var rs = new ModuleResourceSnapshot
                {
                    Id = metric.Key, Label = metric.Label, Category = res.Category,
                    Bytes = metric.HasBytes ? metric.Bytes : 0,
                    Count = metric.Count,
                    Quality = MemQualityText.Code(metric.Quality),
                    Note = metric.Note,
                    Attributed = metric.CountsAsAttributed,
                    ContendedReads = res.ContendedReads,
                };
                if (rs.Attributed) attributed += rs.Bytes;
                else if (metric.Quality == MemQuality.FileSize) informational += rs.Bytes;
                else if (metric.Quality == MemQuality.Throughput && res.Id is "sentBytes") throughput += rs.Bytes;
                snap.Resources.Add(rs);
            }

            foreach (var probe in mod.Probes)
            {
                var metric = probe.ToMetric();
                snap.Resources.Add(new ModuleResourceSnapshot
                {
                    Id = metric.Key, Label = metric.Label, Category = probe.Category,
                    Bytes = metric.Bytes, Count = -1,
                    Quality = MemQualityText.Code(metric.Quality),
                    Note = metric.Note, Attributed = true,
                });
                attributed += metric.Bytes;
            }

            mod.ObserveLifecycle(snap.Active, attributed, nowUtc);
            snap.EverActive = mod.EverActive;
            snap.LifecycleNote = mod.BuildLifecycleNote();
            snap.AttributedBytes = attributed;
            snap.InformationalBytes = informational;
            snap.ThroughputBytes = throughput;
            result.Add(snap);
        }
        return result;
    }
}
