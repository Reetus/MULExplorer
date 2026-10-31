using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     Animation of an item: <see cref="Frames" /> holds <see cref="FrameCount" /> signed offsets
///     added to the item id to get each frame's art; <see cref="FrameInterval" /> is the delay
///     between frames and <see cref="StartInterval" /> the delay before the first (both in client
///     ticks of 100 ms).
/// </summary>
public sealed record AnimDataEntry( int Id, sbyte[] Offsets, byte Unknown, byte FrameCount, byte FrameInterval, byte StartInterval )
{
    /// <summary>The first <see cref="FrameCount" /> offsets.</summary>
    public ReadOnlySpan<sbyte> Frames => Offsets.AsSpan( 0, Math.Min( FrameCount, Offsets.Length ) );

    /// <summary>Item id shown at frame <paramref name="frame" /> (wraps).</summary>
    public int FrameItem( int frame )
    {
        if ( FrameCount == 0 )
        {
            return Id;
        }

        var i = frame % FrameCount;
        return Id + Offsets[Math.Min( i < 0 ? i + FrameCount : i, Offsets.Length - 1 )];
    }

    /// <summary>Delay between frames in milliseconds (client ticks are 100 ms; 0 counts as 1 tick).</summary>
    public int FrameDelayMilliseconds => Math.Max( 1, ( int )FrameInterval ) * 100;
}

/// <summary>
///     <c>animdata.mul</c>: blocks of a 4-byte header followed by 8 entries of 68 bytes
///     (64 signed frame offsets, unknown, frame count, frame interval, start interval). Entry
///     <c>n</c> belongs to item id <c>n</c>; entries with no frames are skipped.
/// </summary>
public sealed class AnimData
{
    private const int EntrySize = 68;
    private const int BlockSize = 4 + 8 * EntrySize;

    private readonly Dictionary<int, AnimDataEntry> _entries = new();

    public AnimData( UOFileSet files )
    {
        Files = files;
        var path = files.Find( "animdata.mul" ) ?? throw new FileNotFoundException( "animdata.mul not found." );
        var data = File.ReadAllBytes( path ).AsSpan();
        var blocks = data.Length / BlockSize;

        for ( var b = 0; b < blocks; b++ )
        {
            var block = data.Slice( b * BlockSize + 4, 8 * EntrySize );

            for ( var i = 0; i < 8; i++ )
            {
                var e = block.Slice( i * EntrySize, EntrySize );
                var count = e[65];

                if ( count == 0 )
                {
                    continue;
                }

                var id = b * 8 + i;
                var offsets = new sbyte[64];
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>( e[..64] ).CopyTo( offsets );
                _entries[id] = new AnimDataEntry( id, offsets, e[64], count, e[66], e[67] );
            }
        }

        Ids = _entries.Keys.Order().ToArray();
        Capacity = blocks * 8;
    }

    public UOFileSet Files { get; }

    /// <summary>Item ids that have an animation, ascending.</summary>
    public IReadOnlyList<int> Ids { get; }

    /// <summary>Number of entry slots in the file.</summary>
    public int Capacity { get; }

    public int Count => _entries.Count;

    public AnimDataEntry? Get( int id ) => _entries.GetValueOrDefault( id );
}
