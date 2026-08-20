namespace VRCNext.Services.Memc;

// Fixed-capacity ring of long values with the timestamp of each sample.
// Backed by two pre-allocated arrays, so sampling never allocates and the
// history can never grow into a memory problem of its own.
public sealed class MemSeries
{
    public const int DefaultCapacity = 900; // 900 samples * 2 s = 30 minutes

    private readonly long[] _values;
    private readonly long[] _ticks;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public long StartValue { get; private set; }
    public long StartTicks { get; private set; }
    public long Min { get; private set; } = long.MaxValue;
    public long Max { get; private set; } = long.MinValue;
    public long TotalSamples { get; private set; }
    private double _sum;

    public string Key { get; }
    public string Label { get; }

    public MemSeries(string key, string label, int capacity = DefaultCapacity)
    {
        Key = key; Label = label;
        _capacity = capacity < 8 ? 8 : capacity;
        _values = new long[_capacity];
        _ticks = new long[_capacity];
    }

    public long BytesOfSelf => 2L * MemorySizer.OfArray(8, _capacity) + 128;

    public void Add(long value, long utcTicks)
    {
        lock (_lock)
        {
            if (_count == 0) { StartValue = value; StartTicks = utcTicks; }
            _values[_head] = value;
            _ticks[_head] = utcTicks;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
            _sum += value;
            TotalSamples++;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _head = 0; _count = 0; _sum = 0; TotalSamples = 0;
            Min = long.MaxValue; Max = long.MinValue;
            StartValue = 0; StartTicks = 0;
        }
    }

    public MemSeriesStats Stats()
    {
        lock (_lock)
        {
            var s = new MemSeriesStats { Key = Key, Label = Label, SampleCount = TotalSamples };
            if (_count == 0) return s;
            var lastIdx = (_head - 1 + _capacity) % _capacity;
            s.Current = _values[lastIdx];
            s.CurrentTicks = _ticks[lastIdx];
            s.Previous = _count > 1 ? _values[(_head - 2 + _capacity) % _capacity] : s.Current;
            s.Start = StartValue;
            s.Min = Min == long.MaxValue ? 0 : Min;
            s.Max = Max == long.MinValue ? 0 : Max;
            s.Average = (long)(_sum / TotalSamples);
            s.Delta = s.Current - s.Previous;
            s.GrowthSinceStart = s.Current - s.Start;
            var minutes = (s.CurrentTicks - StartTicks) / (double)TimeSpan.TicksPerMinute;
            s.WindowMinutes = minutes;
            s.GrowthPerMinute = minutes > 0.25 ? (long)(s.GrowthSinceStart / minutes) : 0;
            s.HasGrowthRate = minutes > 0.25;

            // Trend over the retained window only (the ring may have dropped older samples).
            var oldestIdx = _count == _capacity ? _head : 0;
            var windowStart = _values[oldestIdx];
            var windowTicks = _ticks[oldestIdx];
            var wMin = (s.CurrentTicks - windowTicks) / (double)TimeSpan.TicksPerMinute;
            s.WindowGrowth = s.Current - windowStart;
            s.WindowGrowthPerMinute = wMin > 0.25 ? (long)(s.WindowGrowth / wMin) : 0;
            return s;
        }
    }

    // Downsampled copy for charting / export. Allocates only when explicitly requested.
    public List<long[]> Export(int maxPoints)
    {
        lock (_lock)
        {
            var outList = new List<long[]>(Math.Min(_count, maxPoints));
            if (_count == 0) return outList;
            var step = Math.Max(1, _count / Math.Max(1, maxPoints));
            var oldest = _count == _capacity ? _head : 0;
            for (int i = 0; i < _count; i += step)
            {
                var idx = (oldest + i) % _capacity;
                outList.Add(new[] { _ticks[idx], _values[idx] });
            }
            return outList;
        }
    }
}

public sealed class MemSeriesStats
{
    public string Key = "";
    public string Label = "";
    public long Current, Previous, Start, Min, Max, Average, Delta;
    public long GrowthSinceStart, GrowthPerMinute, WindowGrowth, WindowGrowthPerMinute;
    public long CurrentTicks;
    public long SampleCount;
    public double WindowMinutes;
    public bool HasGrowthRate;

    // Classification used by the Growth Detection panel. Thresholds are byte rates,
    // not opinions: "growing" means the measured slope exceeds 256 KB/min.
    public string Trend
    {
        get
        {
            if (!HasGrowthRate || SampleCount < 4) return "unknown";
            const long threshold = 256 * 1024;
            if (WindowGrowthPerMinute > threshold) return "growing";
            if (WindowGrowthPerMinute < -threshold) return "shrinking";
            return "stable";
        }
    }
}
