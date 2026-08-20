namespace VRCNext.Services.Memc;

// Broad bucket a resource belongs to. Used to group the per-module breakdown in the UI.
public static class MemCategory
{
    public const string Managed  = "managed";
    public const string Buffers  = "buffers";
    public const string Images   = "images";
    public const string Models   = "models";
    public const string Audio    = "audio";
    public const string Database = "database";
    public const string History  = "history";
    public const string Native   = "native";
    public const string Bridge   = "bridge";
}

// A single tracked resource owned by a module.
//
// Bytes/Count are pull-based delegates so nothing is evaluated unless the Memory
// Console is actually sampling. Deep == true means the delegate is expensive
// (walks a JSON tree, etc.) and is only evaluated on an explicit user request.
public sealed class MemResource
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Category { get; init; }
    public required MemQuality Quality { get; init; }
    public bool Deep { get; init; }
    public string? Note { get; init; }
    public Func<long>? Bytes { get; init; }
    public Func<long>? Count { get; init; }

    private long _lastDeepBytes = -1;
    private DateTime _lastDeepAtUtc;
    private long _lastGoodBytes = -1;
    private DateTime _lastGoodAtUtc;
    private int _contendedReads;

    public long LastDeepBytes => _lastDeepBytes;
    public DateTime LastDeepAtUtc => _lastDeepAtUtc;
    public int ContendedReads => _contendedReads;

    public void StoreDeep(long bytes)
    {
        _lastDeepBytes = bytes;
        _lastDeepAtUtc = DateTime.UtcNow;
    }

    public MemMetric Read(bool includeDeep)
    {
        long bytes = 0;
        long count = -1;
        var quality = Quality;
        string? note = Note;

        try { if (Count != null) count = Count(); } catch { count = -1; }

        if (Bytes != null)
        {
            if (Deep && !includeDeep)
            {
                // Reuse the last explicit deep measurement instead of walking again.
                if (_lastDeepBytes >= 0)
                {
                    bytes = _lastDeepBytes;
                    note = (note == null ? "" : note + " ") +
                           $"Deep measure from {_lastDeepAtUtc:HH:mm:ss} UTC.";
                }
                else
                {
                    quality = MemQuality.CountOnly;
                    note = (note == null ? "" : note + " ") + "Run Deep Measure to compute bytes.";
                }
            }
            else if (TryRead(out var read))
            {
                bytes = read;
                _lastGoodBytes = read;
                _lastGoodAtUtc = DateTime.UtcNow;
                if (Deep) StoreDeep(read);
            }
            else if (_lastGoodBytes >= 0)
            {
                // The structure was being mutated on another thread while we walked it.
                // Report the last value we did read cleanly and say so, rather than
                // pretending the resource is unmeasurable.
                bytes = _lastGoodBytes;
                note = (note == null ? "" : note + " ")
                     + $"Value from {_lastGoodAtUtc:HH:mm:ss} UTC; the structure was being modified during this sample.";
            }
            else
            {
                quality = MemQuality.NotMeasurable;
                bytes = 0;
                note = (note == null ? "" : note + " ")
                     + "Could not be walked yet because it is being modified concurrently.";
            }
        }

        return new MemMetric
        {
            Key = Id, Label = Label, Bytes = bytes, Count = count, Quality = quality, Note = note,
        };
    }

    // Walking a plain Dictionary/HashSet while another thread mutates it throws
    // InvalidOperationException. One retry clears the common case; anything else
    // falls back to the last clean value.
    private bool TryRead(out long value)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { value = Bytes!(); return true; }
            catch (InvalidOperationException) { Interlocked.Increment(ref _contendedReads); }
            catch (IndexOutOfRangeException) { Interlocked.Increment(ref _contendedReads); }
            catch (ArgumentException) { Interlocked.Increment(ref _contendedReads); }
            catch (NullReferenceException) { Interlocked.Increment(ref _contendedReads); }
            catch { break; }
        }
        value = 0;
        return false;
    }
}

// A native allocation whose size was measured as a process-memory delta around
// a load/unload. This is the only honest way to attribute unmanaged bytes that
// the CLR knows nothing about.
public sealed class MemNativeProbe
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Category { get; init; }
    public long DeltaBytes { get; set; }
    public long ReleasedBytes { get; set; }
    public bool Held { get; set; }
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime ReleasedAtUtc { get; set; }
    public string? Detail { get; set; }

    /// <summary>True once a load was actually bracketed and measured.</summary>
    public bool EverMeasured { get; set; }

    /// <summary>
    /// True when another native load was in flight during this probe's bracket. The
    /// byte figure then includes foreign allocations and must not be treated as this
    /// resource's size.
    /// </summary>
    public bool Contaminated { get; set; }

    /// <summary>
    /// Reports whether the native resource is currently loaded. Needed to tell
    /// "not loaded" apart from "loaded before the console was switched on", which
    /// would otherwise both show up as 0 bytes.
    /// </summary>
    public Func<bool>? IsLoaded { get; set; }

    public MemMetric ToMetric()
    {
        var loadedNow = false;
        try { loadedNow = IsLoaded?.Invoke() ?? false; } catch { }

        // The resource is in memory but we never saw it being loaded, so there is no
        // delta to report. Saying "0 bytes" here would be a lie by omission.
        if (!EverMeasured && loadedNow)
            return new MemMetric
            {
                Key = Id, Label = Label, Bytes = 0, Count = -1,
                Quality = MemQuality.NotMeasurable,
                Note = "Loaded before the Memory Console was enabled, so no load delta could be "
                     + "measured. Restart this tool while /memc is on to get a figure. Its memory is "
                     + "currently part of the unattributed native bucket.",
            };

        var contaminationWarning = Contaminated
            ? " WARNING: another native load overlapped this measurement, so this figure "
              + "includes foreign allocations. Load the tools one at a time to get a clean value."
            : "";

        return new MemMetric
        {
            Key = Id,
            Label = Label,
            Bytes = Held ? DeltaBytes : 0,
            Count = -1,
            Quality = MemQuality.Probe,
            Note = Held
                ? $"Process private-bytes delta measured at load ({MemorySizer.Human(DeltaBytes)})."
                  + (Detail == null ? "" : " " + Detail) + contaminationWarning
                : EverMeasured
                    ? $"Released at {ReleasedAtUtc:HH:mm:ss} UTC. Acquired {MemorySizer.Human(DeltaBytes)}, "
                      + $"returned {MemorySizer.Human(ReleasedBytes)} to the process." + contaminationWarning
                    : "Not loaded.",
        };
    }
}

// Records what a native load/unload actually cost, by sampling the process before and after.
//
// A delta probe measures the WHOLE process, so if a second native load runs on
// another thread inside this bracket, its bytes land on this probe too. That is
// exactly what happened when Voice Fight and Kikitan loaded seconds apart: the
// VOSK probe swallowed part of the Whisper load and read more than twice its real
// size. The overlap counter below detects that and marks the reading instead of
// presenting a contaminated number as fact.
public sealed class MemProbeScope : IDisposable
{
    private static int _activeScopes;

    private readonly MemNativeProbe _probe;
    private readonly long _beforePrivate;
    private readonly long _beforeManaged;
    private readonly int _entryDepth;
    private bool _done;

    internal MemProbeScope(MemNativeProbe probe)
    {
        _probe = probe;
        _entryDepth = Interlocked.Increment(ref _activeScopes);
        _beforePrivate = MemoryProcessReader.PrivateBytes();
        _beforeManaged = GC.GetTotalMemory(false);
    }

    public void Complete(string? detail = null)
    {
        if (_done) return;
        _done = true;
        var afterPrivate = MemoryProcessReader.PrivateBytes();
        var afterManaged = GC.GetTotalMemory(false);
        var exitDepth = Interlocked.Decrement(ref _activeScopes) + 1;
        // Subtract the managed part so the probe reports the native share only.
        var delta = (afterPrivate - _beforePrivate) - (afterManaged - _beforeManaged);
        _probe.DeltaBytes = Math.Max(0, delta);
        _probe.Held = true;
        _probe.EverMeasured = true;
        _probe.AcquiredAtUtc = DateTime.UtcNow;
        _probe.Detail = detail;
        _probe.Contaminated = _entryDepth > 1 || exitDepth > 1;
    }

    public void Dispose() => Complete();
}
