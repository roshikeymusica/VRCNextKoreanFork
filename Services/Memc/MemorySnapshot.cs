using System.Diagnostics;

namespace VRCNext.Services.Memc;

// Cached Process handle. Refresh() re-reads the OS counters; without it every
// property returns the values from the first read.
public static class MemoryProcessReader
{
    private static readonly Process _proc = Process.GetCurrentProcess();
    private static readonly object _lock = new();

    public static void Refresh() { lock (_lock) { try { _proc.Refresh(); } catch { } } }

    public static long PrivateBytes()
    {
        lock (_lock)
        {
            try { _proc.Refresh(); return _proc.PrivateMemorySize64; } catch { return 0; }
        }
    }

    public static ProcessMetrics Read(bool includeThreadAndHandles)
    {
        lock (_lock)
        {
            var m = new ProcessMetrics();
            try
            {
                _proc.Refresh();
                m.WorkingSet          = _proc.WorkingSet64;
                m.PrivateMemory       = _proc.PrivateMemorySize64;
                m.VirtualMemory       = _proc.VirtualMemorySize64;
                m.PagedMemory         = _proc.PagedMemorySize64;
                m.PagedSystemMemory   = _proc.PagedSystemMemorySize64;
                m.NonpagedSystemMemory= _proc.NonpagedSystemMemorySize64;
                m.PeakWorkingSet      = _proc.PeakWorkingSet64;
                m.PeakVirtualMemory   = _proc.PeakVirtualMemorySize64;
                m.PeakPagedMemory     = _proc.PeakPagedMemorySize64;
                m.HandleCount         = _proc.HandleCount;
                m.StartTimeUtc        = _proc.StartTime.ToUniversalTime();
                m.Valid               = true;
                // Threads allocates a ProcessThreadCollection, so it is sampled less often.
                if (includeThreadAndHandles) m.ThreadCount = _proc.Threads.Count;
                else m.ThreadCount = -1;
            }
            catch { m.Valid = false; }
            return m;
        }
    }
}

public sealed class ProcessMetrics
{
    public bool Valid;
    public long WorkingSet;
    public long PrivateMemory;
    public long VirtualMemory;
    public long PagedMemory;
    public long PagedSystemMemory;
    public long NonpagedSystemMemory;
    public long PeakWorkingSet;
    public long PeakVirtualMemory;
    public long PeakPagedMemory;
    public int  HandleCount;
    public int  ThreadCount = -1;
    public DateTime StartTimeUtc;
}

public sealed class GcMetrics
{
    public bool InfoValid;
    public long TotalManagedMemory;      // GC.GetTotalMemory(false)
    public long TotalAllocatedBytes;     // GC.GetTotalAllocatedBytes(false), cumulative
    public long HeapSize;                // GCMemoryInfo.HeapSizeBytes
    public long TotalCommitted;          // GCMemoryInfo.TotalCommittedBytes
    public long Fragmented;              // GCMemoryInfo.FragmentedBytes
    public long MemoryLoadBytes;
    public long TotalAvailableMemory;
    public long HighMemoryLoadThreshold;
    public long PinnedObjectsCount = -1;
    public long FinalizationPendingCount = -1;
    public int  LastGcGeneration = -1;
    public long LastGcIndex = -1;
    public bool LastGcCompacted;
    public bool LastGcConcurrent;
    public double PauseTimePercentage;
    public int  Gen0Collections;
    public int  Gen1Collections;
    public int  Gen2Collections;
    public bool ServerGc;
    public bool ConcurrentGc;
    // GenerationInfo: 0=gen0 1=gen1 2=gen2 3=LOH 4=POH. -1 means the runtime did not report it.
    public long Gen0Size = -1, Gen1Size = -1, Gen2Size = -1, LohSize = -1, PohSize = -1;
    public long Gen0Frag = -1, Gen1Frag = -1, Gen2Frag = -1, LohFrag = -1, PohFrag = -1;

    public static GcMetrics Read()
    {
        var g = new GcMetrics
        {
            TotalManagedMemory  = GC.GetTotalMemory(false),
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
            Gen0Collections     = GC.CollectionCount(0),
            Gen1Collections     = GC.CollectionCount(1),
            Gen2Collections     = GC.CollectionCount(2),
            ServerGc            = System.Runtime.GCSettings.IsServerGC,
            ConcurrentGc        = System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.Batch,
        };
        try
        {
            var i = GC.GetGCMemoryInfo();
            g.InfoValid               = true;
            g.HeapSize                = i.HeapSizeBytes;
            g.TotalCommitted          = i.TotalCommittedBytes;
            g.Fragmented              = i.FragmentedBytes;
            g.MemoryLoadBytes         = i.MemoryLoadBytes;
            g.TotalAvailableMemory    = i.TotalAvailableMemoryBytes;
            g.HighMemoryLoadThreshold = i.HighMemoryLoadThresholdBytes;
            g.PinnedObjectsCount      = i.PinnedObjectsCount;
            g.FinalizationPendingCount= i.FinalizationPendingCount;
            g.LastGcGeneration        = i.Generation;
            g.LastGcIndex             = i.Index;
            g.LastGcCompacted         = i.Compacted;
            g.LastGcConcurrent        = i.Concurrent;
            g.PauseTimePercentage     = i.PauseTimePercentage;

            var gi = i.GenerationInfo;
            if (gi.Length > 0) { g.Gen0Size = gi[0].SizeAfterBytes; g.Gen0Frag = gi[0].FragmentationAfterBytes; }
            if (gi.Length > 1) { g.Gen1Size = gi[1].SizeAfterBytes; g.Gen1Frag = gi[1].FragmentationAfterBytes; }
            if (gi.Length > 2) { g.Gen2Size = gi[2].SizeAfterBytes; g.Gen2Frag = gi[2].FragmentationAfterBytes; }
            if (gi.Length > 3) { g.LohSize  = gi[3].SizeAfterBytes; g.LohFrag  = gi[3].FragmentationAfterBytes; }
            if (gi.Length > 4) { g.PohSize  = gi[4].SizeAfterBytes; g.PohFrag  = gi[4].FragmentationAfterBytes; }
        }
        catch { g.InfoValid = false; }
        return g;
    }
}

public sealed class ModuleResourceSnapshot
{
    public string Id = "";
    public string Label = "";
    public string Category = "";
    public long Bytes;
    public long Count = -1;
    public string Quality = "";
    public string? Note;
    public bool Attributed;
    public int ContendedReads;
}

public sealed class ModuleSnapshot
{
    public string Id = "";
    public string Label = "";
    public bool Active;
    public bool EverActive;
    public long AttributedBytes;          // Instrumented + Probe only
    public long InformationalBytes;       // FileSize entries, never summed into attribution
    public long ThroughputBytes;          // cumulative flow (bridge), never resident memory
    public List<ModuleResourceSnapshot> Resources = new();
    public string? LifecycleNote;
}

public sealed class MemorySnapshot
{
    public string Name = "";
    public DateTime TakenAtUtc = DateTime.UtcNow;
    public long ProcessUptimeMs;
    public ProcessMetrics Process = new();
    public GcMetrics Gc = new();
    public List<ModuleSnapshot> Modules = new();
    public long TotalAttributedBytes;
    public long UnattributedBytes;
    public long ProfilerSelfAllocBytes;
    public long SampleDurationTicks;
}
