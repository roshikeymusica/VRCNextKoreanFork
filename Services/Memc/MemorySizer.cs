using Newtonsoft.Json.Linq;

namespace VRCNext.Services.Memc;

// Byte sizes computed from the documented 64-bit CoreCLR object layout.
//
//   object header   = 8 bytes sync block index + 8 bytes MethodTable pointer = 16
//   allocation unit = 8 bytes (every object is rounded up to a multiple of 8)
//   string          = 16 + 4 (_stringLength) + 2*Length + 2 (NUL), rounded to 8
//   T[]             = 16 + 8 (length) + Length*sizeof(T),          rounded to 8
//
// These are layout rules, not guesses. Everything produced here is reported as
// MemQuality.Instrumented so the UI can state exactly how the number was derived.
public static class MemorySizer
{
    public const int ObjectHeader = 16;
    public const int RefSize = 8;
    public const int ArrayHeader = 24; // 16 header + 8 length field

    public static long Align8(long n) => (n + 7) & ~7L;

    public static long OfString(string? s)
        => s == null ? 0 : Align8(ObjectHeader + 4 + 2L * s.Length + 2);

    public static long OfByteArray(byte[]? a)
        => a == null ? 0 : Align8(ArrayHeader + a.LongLength);

    public static long OfArray(long elementSize, long count)
        => count <= 0 ? ArrayHeader : Align8(ArrayHeader + elementSize * count);

    // Dictionary<K,V> internals: int[] buckets + Entry[] entries.
    // Entry is { uint hashCode; int next; K key; V value } — 8 bytes + key + value.
    public static long DictionaryOverhead(long count, long keySize = RefSize, long valueSize = RefSize)
    {
        if (count <= 0) return ObjectHeader;
        return ObjectHeader
             + OfArray(4, count)
             + OfArray(Align8(8 + keySize + valueSize), count);
    }

    // HashSet<T>: int[] buckets + Slot[] slots, Slot is { int hashCode; int next; T value }.
    public static long HashSetOverhead(long count, long valueSize = RefSize)
    {
        if (count <= 0) return ObjectHeader;
        return ObjectHeader + OfArray(4, count) + OfArray(Align8(8 + valueSize), count);
    }

    // List<T>: T[] items + count/version fields. Capacity is unknown from outside, Count is the floor.
    public static long ListOverhead(long count, long elementSize = RefSize)
        => ObjectHeader + 16 + OfArray(elementSize, count);

    // Dictionary<string,string> including both key and value payloads.
    public static long OfStringMap(IReadOnlyDictionary<string, string> d)
    {
        long b = DictionaryOverhead(d.Count);
        foreach (var kv in d) b += OfString(kv.Key) + OfString(kv.Value);
        return b;
    }

    // Dictionary<string,(string,string)> — the tuple is inlined into the entry.
    public static long OfStringPairMap(IReadOnlyDictionary<string, (string, string)> d)
    {
        long b = DictionaryOverhead(d.Count, RefSize, RefSize * 2);
        foreach (var kv in d) b += OfString(kv.Key) + OfString(kv.Value.Item1) + OfString(kv.Value.Item2);
        return b;
    }

    public static long OfStringSet(IReadOnlyCollection<string> set)
    {
        long b = HashSetOverhead(set.Count);
        foreach (var s in set) b += OfString(s);
        return b;
    }

    public static long SumStrings(IEnumerable<string?> items)
    {
        long total = 0;
        foreach (var s in items) total += OfString(s);
        return total;
    }

    public static long SumStringKeys<TValue>(IReadOnlyDictionary<string, TValue> d)
    {
        long total = 0;
        foreach (var k in d.Keys) total += OfString(k);
        return total;
    }

    // Deep size of a Newtonsoft JSON tree. Walks every node, so this is only ever run
    // on explicit user request (Deep Measure), never inside the sampling timer.
    // Per-node object sizes come from the JToken class layouts in Newtonsoft.Json 13.
    public static long OfJToken(JToken? token, int depth = 0)
    {
        if (token == null || depth > 64) return 0;
        switch (token.Type)
        {
            case JTokenType.Object:
            {
                long total = ObjectHeader + 24; // JObject: _properties collection ref + parent + annotations
                foreach (var p in (JObject)token)
                {
                    total += ObjectHeader + 24;     // JProperty: name ref, value ref, parent
                    total += OfString(p.Key);
                    total += OfJToken(p.Value, depth + 1);
                }
                return total;
            }
            case JTokenType.Array:
            {
                long total = ObjectHeader + 24;
                foreach (var item in (JArray)token) total += OfJToken(item, depth + 1);
                return total;
            }
            case JTokenType.String:
                return ObjectHeader + 24 + OfString(token.Value<string>());
            case JTokenType.Null:
            case JTokenType.Undefined:
            case JTokenType.None:
                return ObjectHeader + 24;
            default:
                return ObjectHeader + 24; // JValue with a boxed primitive
        }
    }

    public static string Human(long bytes)
    {
        if (bytes < 0) return "-" + Human(-bytes);
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024d;
        if (kb < 1024) return kb.ToString("0.0") + " KB";
        double mb = kb / 1024d;
        if (mb < 1024) return mb.ToString("0.00") + " MB";
        return (mb / 1024d).ToString("0.000") + " GB";
    }
}
