namespace ZeroCdp.Runtime;

/// <summary>
/// Maps a CDP session id to its session without allocating on lookup.
/// </summary>
/// <remarks>
/// A <see cref="Dictionary{TKey,TValue}"/> cannot be probed with a span, and building a
/// <see cref="string"/> per event would allocate on the hot path. This table hashes the raw UTF-8
/// bytes of the id instead, so routing an event never allocates.
/// </remarks>
internal sealed class SessionTable
{
    private const int Tombstone = -1;

    private Entry[] _entries = new Entry[32];
    private int _count;
    private int _occupied;

    public int Count => _count;

    public CdpSession[] Snapshot()
    {
        CdpSession[] sessions = new CdpSession[_count];
        int index = 0;
        foreach (Entry entry in _entries)
        {
            if (entry.Value is not null)
            {
                sessions[index++] = entry.Value;
            }
        }

        return sessions;
    }

    public void Add(ReadOnlySpan<byte> id, CdpSession session)
    {
        if ((_occupied + 1) * 4 >= _entries.Length * 3)
        {
            Resize();
        }

        int index = FindSlot(id);
        ref Entry entry = ref _entries[index];
        if (entry.Key is null)
        {
            _occupied++;
        }

        entry.Hash = Hash(id);
        entry.Key = id.ToArray();
        entry.Value = session;
        _count++;
    }

    public bool TryGet(ReadOnlySpan<byte> id, out CdpSession? session)
    {
        int hash = Hash(id);
        int mask = _entries.Length - 1;
        for (int probe = 0; probe < _entries.Length; probe++)
        {
            ref Entry entry = ref _entries[(hash + probe) & mask];
            if (entry.Key is null)
            {
                // A tombstone keeps the probe chain alive; only a never-used slot ends it.
                if (entry.Hash == Tombstone)
                {
                    continue;
                }

                session = null;
                return false;
            }

            if (entry.Hash == hash && id.SequenceEqual(entry.Key))
            {
                session = entry.Value;
                return true;
            }
        }

        session = null;
        return false;
    }

    public bool Remove(ReadOnlySpan<byte> id)
    {
        int hash = Hash(id);
        int mask = _entries.Length - 1;
        for (int probe = 0; probe < _entries.Length; probe++)
        {
            ref Entry entry = ref _entries[(hash + probe) & mask];
            if (entry.Key is null)
            {
                if (entry.Hash == Tombstone)
                {
                    continue;
                }

                return false;
            }

            if (entry.Hash == hash && id.SequenceEqual(entry.Key))
            {
                entry.Hash = Tombstone;
                entry.Key = null;
                entry.Value = null;
                _count--;
                return true;
            }
        }

        return false;
    }

    private int FindSlot(ReadOnlySpan<byte> id)
    {
        int hash = Hash(id);
        int mask = _entries.Length - 1;
        int firstTombstone = -1;
        for (int probe = 0; probe < _entries.Length; probe++)
        {
            int index = (hash + probe) & mask;
            ref Entry entry = ref _entries[index];
            if (entry.Key is null)
            {
                if (entry.Hash != Tombstone)
                {
                    return firstTombstone >= 0 ? firstTombstone : index;
                }

                if (firstTombstone < 0)
                {
                    firstTombstone = index;
                }

                continue;
            }

            if (entry.Hash == hash && id.SequenceEqual(entry.Key))
            {
                return index;
            }
        }

        return firstTombstone >= 0 ? firstTombstone : 0;
    }

    private void Resize()
    {
        Entry[] previous = _entries;
        _entries = new Entry[previous.Length * 2];
        _count = 0;
        _occupied = 0;

        foreach (Entry entry in previous)
        {
            if (entry.Key is not null)
            {
                Add(entry.Key, entry.Value!);
            }
        }
    }

    /// <summary>FNV-1a over the id bytes, remapped so 0 (empty) and -1 (tombstone) never occur.</summary>
    private static int Hash(ReadOnlySpan<byte> id)
    {
        uint hash = 2166136261;
        foreach (byte b in id)
        {
            hash = (hash ^ b) * 16777619;
        }

        int result = (int)(hash & 0x7FFF_FFFF);
        return result == 0 ? 1 : result;
    }

    private struct Entry
    {
        public int Hash;
        public byte[]? Key;
        public CdpSession? Value;
    }
}
