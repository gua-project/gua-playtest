using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace Gua.Playtest.TestSupport;

// Test transport only. No game/Plan/credential text is accepted by the ledger.
public sealed class ReplayTimingJournal : IDisposable
{
    const int Header = 4096, RowBytes = 64, Segments = 3;
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    readonly int capacity, segment;
    int count, errors;
    ReplayTimingJournal(string path, int capacity, int segment)
    {
        this.capacity = capacity; this.segment = segment;
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
        view = map.CreateViewAccessor();
    }
    public static ReplayTimingJournal Create(string pidPath, int capacity = 1024)
    {
        capacity = Math.Clamp(capacity, 1, 1024);
        string path = pidPath + ".timing";
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            file.SetLength(Header + Segments * capacity * RowBytes);
        var journal = new ReplayTimingJournal(path, capacity, 0);
        journal.view.Write(0, Stopwatch.GetTimestamp()); journal.view.Write(8, Stopwatch.Frequency);
        journal.view.Write(16, 1); journal.view.Write(24, capacity);
        return journal;
    }
    public static ReplayTimingJournal? OpenHost(string pidPath)
    {
        string path = pidPath + ".timing";
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(file);
            file.Position = 8; long frequency = reader.ReadInt64(); int segment = reader.ReadInt32();
            file.Position = 24; int capacity = reader.ReadInt32();
            if (frequency != Stopwatch.Frequency || segment is < 1 or > 2 || capacity is < 1 or > 1024) return null;
            var journal = new ReplayTimingJournal(path, capacity, segment);
            journal.Mark(1, a: frequency);
            return journal;
        }
        catch { return null; } // Missing host records remain explicitly unknown.
    }
    public void NextHost() => view.Write(16, 2);
    public void Mark(long phase, long operation = 0, long a = 0, long b = 0, long c = 0, long? at = null)
    {
        try
        {
            int slot = Interlocked.Increment(ref count) - 1;
            if (slot >= capacity) { view.Write(68 + segment * 16, 1); return; }
            long offset = Header + (segment * capacity + slot) * RowBytes;
            view.Write(offset, at ?? Stopwatch.GetTimestamp()); view.Write(offset + 8, Environment.ProcessId);
            view.Write(offset + 16, phase); view.Write(offset + 24, operation);
            view.Write(offset + 32, a); view.Write(offset + 40, b); view.Write(offset + 48, c);
            Thread.MemoryBarrier(); view.Write(offset + 56, slot + 1L);
        }
        catch { Interlocked.Increment(ref errors); }
    }
    public object Snapshot()
    {
        long origin = view.ReadInt64(0);
        var rows = new List<object>(); var segments = new List<object>();
        for (int part = 0; part < Segments; part++)
        {
            int attempted = view.ReadInt32(64 + part * 16), committed = 0;
            for (int slot = 0; slot < capacity; slot++)
            {
                long offset = Header + (part * capacity + slot) * RowBytes;
                if (view.ReadInt64(offset + 56) != slot + 1L) continue;
                Thread.MemoryBarrier();
                committed++;
                attempted = Math.Max(attempted, slot + 1);
                rows.Add(new { segment = part, slot, ticks = view.ReadInt64(offset) - origin,
                    process = view.ReadInt64(offset + 8), phase = view.ReadInt64(offset + 16),
                    operation = view.ReadInt64(offset + 24), a = view.ReadInt64(offset + 32),
                    b = view.ReadInt64(offset + 40), c = view.ReadInt64(offset + 48) });
            }
            segments.Add(new { segment = part, attemptedAtLeast = attempted, committed,
                dropped = view.ReadInt32(68 + part * 16) != 0, missing = committed == 0,
                errors = part == segment ? Volatile.Read(ref errors) : view.ReadInt32(72 + part * 16) });
        }
        return new { schema = "playtest.test-replay-timing.v1", frequency = Stopwatch.Frequency, capacity,
            timebase = "same Windows QPC origin across processes; a/b clock fields use TimeSpan ticks",
            missingMeaning = "not observed; missing/partial/dropped records are not proof of never-started",
            segments, rows };
    }
    public void Dispose()
    {
        try { view.Write(64 + segment * 16, count); view.Write(72 + segment * 16, errors); }
        catch { }
        view.Dispose(); map.Dispose();
    }
}
