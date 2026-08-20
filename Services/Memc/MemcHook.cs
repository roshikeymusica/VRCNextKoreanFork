namespace VRCNext.Services.Memc;

// Zero-cost attach point for services that own native allocations.
//
// A service keeps a nullable MemModule reference. While the Memory Console is off
// that reference is null and every call here is a null check, so nothing is measured
// and nothing is allocated. When the console is on, the load and unload paths are
// bracketed and the real process-memory delta is recorded against the module.
public static class MemcHook
{
    public static MemNativeProbe? Probe(MemModule? module, string id, string label, string category,
                                        Func<bool>? isLoaded = null)
    {
        var p = module?.Probe(id, label, category);
        if (p != null && isLoaded != null) p.IsLoaded = isLoaded;
        return p;
    }

    /// <summary>Brackets a native load. Call Complete() once the resource is in memory.</summary>
    public static MemProbeScope? BeginLoad(MemNativeProbe? probe)
        => probe == null ? null : new MemProbeScope(probe);

    /// <summary>Brackets a native release so the console can show whether memory actually came back.</summary>
    public static MemReleaseScope? BeginRelease(MemNativeProbe? probe)
        => probe == null ? null : new MemReleaseScope(probe);

    /// <summary>Marks a probe as released without measuring, for paths that cannot bracket the free.</summary>
    public static void MarkReleased(MemNativeProbe? probe)
    {
        if (probe == null) return;
        probe.Held = false;
        probe.EverMeasured = true;
        probe.ReleasedAtUtc = DateTime.UtcNow;
    }
}

public sealed class MemReleaseScope : IDisposable
{
    private readonly MemNativeProbe _probe;
    private readonly long _beforePrivate;
    private readonly long _beforeManaged;
    private bool _done;

    internal MemReleaseScope(MemNativeProbe probe)
    {
        _probe = probe;
        _beforePrivate = MemoryProcessReader.PrivateBytes();
        _beforeManaged = GC.GetTotalMemory(false);
    }

    public void Complete()
    {
        if (_done) return;
        _done = true;
        var afterPrivate = MemoryProcessReader.PrivateBytes();
        var afterManaged = GC.GetTotalMemory(false);
        var freed = (_beforePrivate - afterPrivate) - (_beforeManaged - afterManaged);
        _probe.ReleasedBytes = Math.Max(0, freed);
        _probe.Held = false;
        _probe.EverMeasured = true;
        _probe.ReleasedAtUtc = DateTime.UtcNow;
    }

    public void Dispose() => Complete();
}
