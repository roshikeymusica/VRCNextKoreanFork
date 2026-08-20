namespace VRCNext.Services.Memc;

// How a number in the Memory Console was obtained.
// Every value shown in the UI carries one of these so nothing can be mistaken for a guess.
public enum MemQuality
{
    // Read directly from the OS via System.Diagnostics.Process. Exact.
    Measured = 0,
    // Read from a .NET runtime GC API (GC.GetGCMemoryInfo, GC.CollectionCount, ...).
    RuntimeCounter = 1,
    // Computed by our own sizer walking a registered structure using documented CLR layout.
    Instrumented = 2,
    // Process-level delta measured around a native load/unload. Real bytes, coarse attribution.
    Probe = 3,
    // Size of a file that was handed to a native loader. NOT resident RAM.
    FileSize = 4,
    // Item count is exact, byte size is not computed.
    CountOnly = 5,
    // Cannot be measured with the available APIs. Never shown as a number.
    NotMeasurable = 6,
    // Exact cumulative bytes that flowed through a path (e.g. the WebView bridge).
    // This is a FLOW, not resident memory, so it never counts toward attribution.
    Throughput = 7,
}

public static class MemQualityText
{
    public static string Code(MemQuality q) => q switch
    {
        MemQuality.Measured       => "measured",
        MemQuality.RuntimeCounter => "runtime",
        MemQuality.Instrumented   => "instrumented",
        MemQuality.Probe          => "probe",
        MemQuality.FileSize       => "filesize",
        MemQuality.CountOnly      => "count",
        MemQuality.Throughput     => "throughput",
        _                         => "unmeasurable",
    };

    public static string Describe(MemQuality q) => q switch
    {
        MemQuality.Measured       => "Read from the OS process API. Exact.",
        MemQuality.RuntimeCounter => "Reported by the .NET GC runtime.",
        MemQuality.Instrumented   => "Computed by VRCNext by walking the live structure.",
        MemQuality.Probe          => "Process memory delta measured around a native operation.",
        MemQuality.FileSize       => "Size of the file on disk. Not necessarily resident RAM.",
        MemQuality.CountOnly      => "Item count is exact. Byte size not computed.",
        MemQuality.Throughput     => "Exact cumulative bytes pushed through this path. A flow, not resident memory.",
        _                         => "Not measurable with the available APIs.",
    };
}

// One measured figure. Bytes is only meaningful when HasBytes is true.
public sealed class MemMetric
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public long Bytes { get; init; }
    public long Count { get; init; } = -1;
    public MemQuality Quality { get; init; } = MemQuality.NotMeasurable;
    public string? Note { get; init; }

    public bool HasBytes => Quality is MemQuality.Measured or MemQuality.RuntimeCounter
                                    or MemQuality.Instrumented or MemQuality.Probe or MemQuality.FileSize
                                    or MemQuality.Throughput;

    // Only these count toward "explained" process memory. FileSize and CountOnly never do.
    public bool CountsAsAttributed => Quality is MemQuality.Instrumented or MemQuality.Probe;

    public bool HasCount => Count >= 0;
}
