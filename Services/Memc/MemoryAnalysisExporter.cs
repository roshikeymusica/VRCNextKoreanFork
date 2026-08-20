using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace VRCNext.Services.Memc;

// Writes a reproducible analysis pair (JSON + TXT) so two runs can be compared later.
public static class MemoryAnalysisExporter
{
    public static string ExportDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "Logs", "Memory");

    public sealed class ExportResult
    {
        public string JsonPath = "";
        public string TextPath = "";
        public string Folder = "";
        public long JsonBytes;
    }

    public static ExportResult Export(MemoryManager mgr)
    {
        Directory.CreateDirectory(ExportDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var jsonPath = Path.Combine(ExportDir, $"MemoryAnalysis_{stamp}.json");
        var textPath = Path.Combine(ExportDir, $"MemoryAnalysis_{stamp}.txt");

        // Always take a fresh non-live snapshot so thread count and deep sizes are included.
        var snap = mgr.BuildSnapshot("export", deep: true);
        var model = BuildModel(mgr, snap);

        var json = JsonConvert.SerializeObject(model, Formatting.Indented);
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        File.WriteAllText(textPath, BuildText(mgr, snap, model), new UTF8Encoding(false));

        return new ExportResult
        {
            JsonPath = jsonPath, TextPath = textPath, Folder = ExportDir,
            JsonBytes = new FileInfo(jsonPath).Length,
        };
    }

    private static object BuildModel(MemoryManager mgr, MemorySnapshot snap)
    {
        var series = new List<object>();
        foreach (var s in mgr.AllSeries)
        {
            var st = s.Stats();
            series.Add(new
            {
                key = st.Key,
                label = st.Label,
                current = st.Current,
                start = st.Start,
                min = st.Min,
                max = st.Max,
                avg = st.Average,
                growthSinceStart = st.GrowthSinceStart,
                growthPerMinute = st.GrowthPerMinute,
                windowGrowthPerMinute = st.WindowGrowthPerMinute,
                windowMinutes = Math.Round(st.WindowMinutes, 2),
                sampleCount = st.SampleCount,
                trend = st.Trend,
                points = s.Export(240),
            });
        }

        var probes = new List<object>();
        if (mgr.Modules != null)
        {
            foreach (var res in snap.Modules)
                foreach (var r in res.Resources)
                    if (r.Quality == "probe" || r.Quality == "filesize")
                        probes.Add(new { module = res.Label, r.Label, r.Category, r.Bytes, r.Quality, r.Note });
        }

        Dictionary<string, object?> gcConfig;
        try
        {
            gcConfig = new Dictionary<string, object?>();
            foreach (var kv in GC.GetConfigurationVariables()) gcConfig[kv.Key] = kv.Value;
        }
        catch { gcConfig = new Dictionary<string, object?> { ["error"] = "GC.GetConfigurationVariables unavailable" }; }

        return new
        {
            schema = "vrcnext.memc.analysis/1",
            generatedAtLocal = DateTime.Now.ToString("o"),
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            app = new
            {
                version = AppInfo.Version,
                executable = AppInfo.SelfExecutable,
                userAgent = AppInfo.UserAgent,
            },
            runtime = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osDescription = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                is64BitProcess = Environment.Is64BitProcess,
                processorCount = Environment.ProcessorCount,
                serverGc = System.Runtime.GCSettings.IsServerGC,
                latencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
                gcConfiguration = gcConfig,
            },
            console = new
            {
                intervalMs = mgr.IntervalMs,
                sampleCount = mgr.SampleCount,
                seriesCount = mgr.AllSeries.Count(),
                historyBytes = mgr.HistoryBytes(),
                selfAllocPerSample = mgr.SelfAllocPerSample,
            },
            process = new
            {
                uptimeMs = snap.ProcessUptimeMs,
                startedAtUtc = snap.Process.StartTimeUtc.ToString("o"),
                metrics = MemoryPayloadBuilder.ProcessRows(snap.Process),
            },
            gc = new
            {
                metrics = MemoryPayloadBuilder.GcRows(snap.Gc, mgr.AllocRatePerSecond()),
                collections = new { gen0 = snap.Gc.Gen0Collections, gen1 = snap.Gc.Gen1Collections, gen2 = snap.Gc.Gen2Collections },
                lastGc = new
                {
                    index = snap.Gc.LastGcIndex,
                    generation = snap.Gc.LastGcGeneration,
                    compacted = snap.Gc.LastGcCompacted,
                    concurrent = snap.Gc.LastGcConcurrent,
                    pauseTimePercentage = snap.Gc.PauseTimePercentage,
                },
            },
            attribution = MemoryPayloadBuilder.Attribution(mgr, snap),
            modules = snap.Modules,
            nativeAllocations = probes,
            loadedNativeModules = LoadedNativeModules(),
            activeTools = SafeTools(mgr),
            snapshots = new
            {
                baseline = mgr.Baseline,
                a = mgr.SnapshotA,
                b = mgr.SnapshotB,
            },
            comparison = mgr.BuildComparePayload(),
            gcCompare = mgr.LastGcCompare,
            sampleHistory = series,
            qualityLegend = Enum.GetValues<MemQuality>().Select(q => new
            {
                code = MemQualityText.Code(q),
                description = MemQualityText.Describe(q),
            }),
        };
    }

    private static Dictionary<string, bool> SafeTools(MemoryManager mgr)
    {
        try { return mgr.ActiveToolsProvider?.Invoke() ?? new Dictionary<string, bool>(); }
        catch { return new Dictionary<string, bool>(); }
    }

    // Native DLLs that are known to hold large unmanaged allocations, so an export
    // shows whether a model runtime was actually loaded at the time.
    private static List<object> LoadedNativeModules()
    {
        var result = new List<object>();
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
            {
                var name = m.ModuleName ?? "";
                if (name.Contains("llama", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("ggml", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("vosk", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("d3d11", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("openvr", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("EmbeddedBrowserWebView", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new { name, size = m.ModuleMemorySize, path = m.FileName });
                }
            }
        }
        catch { }
        return result;
    }

    private static string BuildText(MemoryManager mgr, MemorySnapshot snap, object model)
    {
        string H(long b) => MemorySizer.Human(b);
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("VRCNext Memory Analysis");
        sb.AppendLine("=======================");
        sb.AppendLine($"Generated      : {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine($"VRCNext        : {AppInfo.Version}");
        sb.AppendLine($"Runtime        : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS             : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Process uptime : {TimeSpan.FromMilliseconds(snap.ProcessUptimeMs):d\\.hh\\:mm\\:ss}");
        sb.AppendLine($"Server GC      : {System.Runtime.GCSettings.IsServerGC}");
        sb.AppendLine($"Console        : every {mgr.IntervalMs} ms, {mgr.SampleCount} samples, "
                    + $"history {H(mgr.HistoryBytes())}, self-alloc {H(mgr.SelfAllocPerSample)}/sample");
        sb.AppendLine();

        sb.AppendLine("PROCESS MEMORY  [measured — System.Diagnostics.Process]");
        sb.AppendLine($"  Working Set              {H(snap.Process.WorkingSet)}");
        sb.AppendLine($"  Private Memory           {H(snap.Process.PrivateMemory)}");
        sb.AppendLine($"  Virtual Memory           {H(snap.Process.VirtualMemory)}");
        sb.AppendLine($"  Paged Memory             {H(snap.Process.PagedMemory)}");
        sb.AppendLine($"  Paged System Memory      {H(snap.Process.PagedSystemMemory)}");
        sb.AppendLine($"  Non Paged System Memory  {H(snap.Process.NonpagedSystemMemory)}");
        sb.AppendLine($"  Peak Working Set         {H(snap.Process.PeakWorkingSet)}");
        sb.AppendLine($"  Peak Virtual Memory      {H(snap.Process.PeakVirtualMemory)}");
        sb.AppendLine($"  Handles                  {snap.Process.HandleCount}");
        sb.AppendLine($"  Threads                  {(snap.Process.ThreadCount >= 0 ? snap.Process.ThreadCount.ToString() : "not sampled")}");
        sb.AppendLine();

        sb.AppendLine("GC  [runtime counters — GC.GetGCMemoryInfo / GC.*]");
        sb.AppendLine($"  Heap Size                {H(snap.Gc.HeapSize)}");
        sb.AppendLine($"  Total Managed Memory     {H(snap.Gc.TotalManagedMemory)}");
        sb.AppendLine($"  Committed                {H(snap.Gc.TotalCommitted)}");
        sb.AppendLine($"  Reserved                 not measurable (no .NET API)");
        sb.AppendLine($"  Fragmentation            {H(snap.Gc.Fragmented)}");
        sb.AppendLine($"  Gen 0 / 1 / 2            {H(Math.Max(0, snap.Gc.Gen0Size))} / {H(Math.Max(0, snap.Gc.Gen1Size))} / {H(Math.Max(0, snap.Gc.Gen2Size))}");
        sb.AppendLine($"  LOH / POH                {H(Math.Max(0, snap.Gc.LohSize))} / {H(Math.Max(0, snap.Gc.PohSize))}");
        sb.AppendLine($"  Collections g0/g1/g2     {snap.Gc.Gen0Collections} / {snap.Gc.Gen1Collections} / {snap.Gc.Gen2Collections}");
        sb.AppendLine($"  Allocated total          {H(snap.Gc.TotalAllocatedBytes)}");
        var rate = mgr.AllocRatePerSecond();
        sb.AppendLine($"  Allocation rate          {(rate >= 0 ? H(rate) + "/s" : "not enough samples")}");
        sb.AppendLine($"  Finalization queue       {(snap.Gc.FinalizationPendingCount >= 0 ? snap.Gc.FinalizationPendingCount.ToString() : "not measurable")}");
        sb.AppendLine($"  Pinned objects           {(snap.Gc.PinnedObjectsCount >= 0 ? snap.Gc.PinnedObjectsCount.ToString() : "not measurable")}");
        sb.AppendLine($"  GC handles               not measurable (no public .NET API)");
        sb.AppendLine();

        var managed = MemoryManager.ManagedAttributed(snap);
        var native = MemoryManager.NativeAttributed(snap);
        var nonGc = Math.Max(0, snap.Process.PrivateMemory - snap.Gc.TotalCommitted);
        sb.AppendLine("ATTRIBUTION");
        sb.AppendLine($"  GC heap                          {H(snap.Gc.HeapSize)}");
        sb.AppendLine($"    attributed by instrumentation  {H(managed)}");
        sb.AppendLine($"    NOT attributed (managed)       {H(Math.Max(0, snap.Gc.HeapSize - managed))}");
        sb.AppendLine($"  Non-GC process commit            {H(nonGc)}   (private - GC committed)");
        sb.AppendLine($"    attributed by native probes    {H(native)}");
        sb.AppendLine($"    NOT attributed (native)        {H(Math.Max(0, nonGc - native))}");
        sb.AppendLine();

        sb.AppendLine("MODULES");
        foreach (var m in snap.Modules.OrderByDescending(m => m.AttributedBytes))
        {
            sb.AppendLine($"  {m.Label}  [{(m.Active ? "running" : m.EverActive ? "stopped" : "idle")}]  attributed {H(m.AttributedBytes)}");
            if (m.LifecycleNote != null) sb.AppendLine($"      lifecycle: {m.LifecycleNote}");
            foreach (var r in m.Resources.OrderByDescending(r => r.Bytes))
            {
                var cnt = r.Count >= 0 ? $"  n={r.Count}" : "";
                var val = r.Quality is "count" or "unmeasurable" ? "-" : H(r.Bytes);
                sb.AppendLine($"      {r.Label,-38} {val,12}  [{r.Quality}]{cnt}");
                if (r.Note != null) sb.AppendLine($"          {r.Note}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("GROWTH  (measured slope over the retained sample window)");
        foreach (var s in mgr.AllSeries.Select(s => s.Stats())
                             .Where(s => s.SampleCount >= 4)
                             .OrderByDescending(s => Math.Abs(s.WindowGrowthPerMinute))
                             .Take(40))
        {
            sb.AppendLine($"  {s.Label,-46} {H(s.Current),12}   {s.Trend,-9} "
                        + $"{(s.WindowGrowthPerMinute >= 0 ? "+" : "")}{H(s.WindowGrowthPerMinute)}/min  "
                        + $"peak {H(s.Max)}  n={s.SampleCount}");
        }
        sb.AppendLine();

        if (mgr.SnapshotA != null && mgr.SnapshotB != null)
        {
            var a = mgr.SnapshotA; var b = mgr.SnapshotB;
            sb.AppendLine("A/B COMPARISON");
            sb.AppendLine($"  A taken {a.TakenAtUtc:HH:mm:ss} UTC, B taken {b.TakenAtUtc:HH:mm:ss} UTC "
                        + $"({(b.TakenAtUtc - a.TakenAtUtc).TotalMinutes:0.0} min apart)");
            void D(string label, long av, long bv) =>
                sb.AppendLine($"  {label,-32} {H(av),12} -> {H(bv),12}   {(bv - av >= 0 ? "+" : "")}{H(bv - av)}");
            D("Working Set", a.Process.WorkingSet, b.Process.WorkingSet);
            D("Private Memory", a.Process.PrivateMemory, b.Process.PrivateMemory);
            D("GC Heap", a.Gc.HeapSize, b.Gc.HeapSize);
            D("GC Committed", a.Gc.TotalCommitted, b.Gc.TotalCommitted);
            D("LOH", Math.Max(0, a.Gc.LohSize), Math.Max(0, b.Gc.LohSize));
            sb.AppendLine();
            var modA = a.Modules.ToDictionary(m => m.Id);
            foreach (var mb in b.Modules.OrderByDescending(m => m.AttributedBytes))
            {
                modA.TryGetValue(mb.Id, out var ma);
                var d = mb.AttributedBytes - (ma?.AttributedBytes ?? 0);
                if (d == 0 && mb.AttributedBytes == 0) continue;
                sb.AppendLine($"  {mb.Label,-32} {H(ma?.AttributedBytes ?? 0),12} -> {H(mb.AttributedBytes),12}   {(d >= 0 ? "+" : "")}{H(d)}");
            }
            var mgdA = MemoryManager.ManagedAttributed(a);
            var mgdB = MemoryManager.ManagedAttributed(b);
            var natA = MemoryManager.NativeAttributed(a);
            var natB = MemoryManager.NativeAttributed(b);
            var privD = b.Process.PrivateMemory - a.Process.PrivateMemory;
            var gcOv = (b.Gc.TotalCommitted - b.Gc.HeapSize) - (a.Gc.TotalCommitted - a.Gc.HeapSize);
            var expl = (mgdB - mgdA) + (natB - natA) + gcOv;
            sb.AppendLine();
            sb.AppendLine($"  Process private delta            {(privD >= 0 ? "+" : "")}{H(privD)}");
            sb.AppendLine($"    explained by instrumentation   {(expl >= 0 ? "+" : "")}{H(expl)}");
            sb.AppendLine($"    UNATTRIBUTED                   {(privD - expl >= 0 ? "+" : "")}{H(privD - expl)}");
            sb.AppendLine();
        }

        if (mgr.LastGcCompare is GcCompareResult gcc)
        {
            sb.AppendLine("FORCED GC");
            sb.AppendLine($"  Ran at {gcc.AtUtc:HH:mm:ss} UTC");
            sb.AppendLine($"  Reclaimable managed memory       {H(gcc.Before.Gc.HeapSize - gcc.After.Gc.HeapSize)}");
            sb.AppendLine($"  Still referenced managed memory  {H(gcc.After.Gc.HeapSize)}");
            sb.AppendLine($"  Native memory after collection   {H(Math.Max(0, gcc.After.Process.PrivateMemory - gcc.After.Gc.TotalCommitted))}");
            sb.AppendLine($"  Working set before -> after      {H(gcc.Before.Process.WorkingSet)} -> {H(gcc.After.Process.WorkingSet)}");
            sb.AppendLine();
        }

        var natives = LoadedNativeModules();
        if (natives.Count > 0)
        {
            sb.AppendLine("LOADED NATIVE MODULES OF INTEREST");
            foreach (dynamic n in natives) sb.AppendLine($"  {n.name,-34} {H((long)n.size),10}");
            sb.AppendLine();
        }

        sb.AppendLine("MEASUREMENT QUALITY LEGEND");
        foreach (var q in Enum.GetValues<MemQuality>())
            sb.AppendLine($"  {MemQualityText.Code(q),-14} {MemQualityText.Describe(q)}");
        return sb.ToString();
    }
}
