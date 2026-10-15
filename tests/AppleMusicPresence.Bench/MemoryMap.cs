using System.Runtime.InteropServices;

namespace AppleMusicPresence.Bench;

internal sealed record MemoryBucket(string Name, double CommittedMB, double ResidentMB, double ResidentPrivateMB, double ResidentShareableMB);

internal sealed record MemoryMapReport(
    double CommittedMB, double ResidentMB, double ResidentPrivateMB, double ResidentShareableMB,
    MemoryBucket[] Categories, MemoryBucket[] ImageFiles, MemoryBucket[] MappedFiles, string[] Modules);

/// <summary>
/// Classifies every committed page of a process. Residency and sharing come from
/// QueryWorkingSetEx per page. Categories:
///   image                     mapped executable images (by file)
///   mapped-file               named file mappings (by file)
///   section-assembly-copy     pagefile-backed sections starting with "MZ" (flat copies of bundled assemblies)
///   cfg-bitmap                the Control Flow Guard bitmap the OS maps into every process (multi-TB reservation)
///   section-other             other pagefile-backed sections (shared memory with other processes/drivers)
///   private-pe-copy           private allocations starting with "MZ" (converted images of bundled assemblies)
///   private-executable        other executable private memory (JIT code, stubs)
///   private-data              heaps, GC heap, stacks and other private data
/// </summary>
internal static class MemoryMap
{
    private const double MB = 1024 * 1024;
    private const int Page = 4096;
    private const long Terabyte = 1L << 40;

    private sealed class Totals
    {
        public long Committed, Resident, ResidentPrivate, ResidentShareable;
        public MemoryBucket ToBucket(string name) => new(name, Committed / MB, Resident / MB, ResidentPrivate / MB, ResidentShareable / MB);
    }

    public static MemoryMapReport Capture(nint process)
    {
        var regions = new List<Native.MemoryBasicInformation>();
        var reserved = new Dictionary<nint, long>();
        nint address = 0;
        var infoSize = (nuint)Marshal.SizeOf<Native.MemoryBasicInformation>();
        while (Native.VirtualQueryEx(process, address, out var region, infoSize) != 0)
        {
            if (region.State != 0x10000) // MEM_FREE
            {
                regions.Add(region);
                reserved[region.AllocationBase] = reserved.GetValueOrDefault(region.AllocationBase) + (long)region.RegionSize;
            }
            var next = region.BaseAddress + (nint)region.RegionSize;
            if (next <= address) break;
            address = next;
        }

        var categories = new Dictionary<string, Totals>();
        var images = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var mapped = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var all = new Totals();
        var peHeaders = new Dictionary<nint, bool>();
        var name = new char[1024];
        foreach (var region in regions.Where(r => r.State == Native.MemCommit))
        {
            string category;
            string? file = null;
            if (region.Type is Native.MemImage or Native.MemMapped)
            {
                var length = Native.GetMappedFileNameW(process, region.BaseAddress, name, (uint)name.Length);
                if (length > 0) file = Path.GetFileName(new string(name, 0, (int)length));
                category = region.Type == Native.MemImage ? "image"
                    : file is not null ? "mapped-file"
                    : reserved[region.AllocationBase] >= Terabyte ? "cfg-bitmap"
                    : StartsWithPeHeader(process, region.AllocationBase, peHeaders) ? "section-assembly-copy"
                    : "section-other";
            }
            else if (StartsWithPeHeader(process, region.AllocationBase, peHeaders)) category = "private-pe-copy";
            else if ((region.Protect & Native.ExecuteProtection) != 0) category = "private-executable";
            else category = "private-data";

            var totals = Measure(process, region);
            Add(categories, category, totals);
            if (file is not null) Add(region.Type == Native.MemImage ? images : mapped, file, totals);
            Add(all, totals);
        }
        static MemoryBucket[] Top(Dictionary<string, Totals> source, int count) => source
            .OrderByDescending(pair => pair.Value.Resident).ThenByDescending(pair => pair.Value.Committed)
            .Take(count).Select(pair => pair.Value.ToBucket(pair.Key)).ToArray();
        return new(all.Committed / MB, all.Resident / MB, all.ResidentPrivate / MB, all.ResidentShareable / MB,
            Top(categories, 16), Top(images, 30), Top(mapped, 12), [.. images.Keys.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    private static Totals Measure(nint process, Native.MemoryBasicInformation region)
    {
        var totals = new Totals { Committed = (long)region.RegionSize };
        var pages = (int)((long)region.RegionSize / Page);
        const int batch = 16384;
        var buffer = new Native.WorkingSetExInformation[Math.Min(pages, batch)];
        for (var start = 0; start < pages; start += batch)
        {
            var count = Math.Min(batch, pages - start);
            for (var i = 0; i < count; i++) buffer[i] = new() { VirtualAddress = region.BaseAddress + (nint)(start + i) * Page };
            if (!Native.QueryWorkingSetEx(process, buffer, (uint)(count * Marshal.SizeOf<Native.WorkingSetExInformation>()))) continue;
            for (var i = 0; i < count; i++)
            {
                var attributes = (ulong)buffer[i].VirtualAttributes;
                if ((attributes & 1) == 0) continue;
                totals.Resident += Page;
                if ((attributes & (1UL << 15)) != 0) totals.ResidentShareable += Page;
                else totals.ResidentPrivate += Page;
            }
        }
        return totals;
    }

    private static bool StartsWithPeHeader(nint process, nint allocationBase, Dictionary<nint, bool> cache)
    {
        if (cache.TryGetValue(allocationBase, out var known)) return known;
        var header = new byte[2];
        var result = Native.ReadProcessMemory(process, allocationBase, header, 2, out var read) && read == 2 &&
            header[0] == (byte)'M' && header[1] == (byte)'Z';
        cache[allocationBase] = result;
        return result;
    }

    private static void Add(Dictionary<string, Totals> map, string key, Totals value)
    {
        if (!map.TryGetValue(key, out var totals)) map[key] = totals = new();
        Add(totals, value);
    }

    private static void Add(Totals target, Totals value)
    {
        target.Committed += value.Committed;
        target.Resident += value.Resident;
        target.ResidentPrivate += value.ResidentPrivate;
        target.ResidentShareable += value.ResidentShareable;
    }
}
