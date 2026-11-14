using UODataAccess.IO;

namespace UODataAccess.Merge;

/// <summary>A run of fixed-size records, optionally grouped behind a per-group header.</summary>
/// <param name="Name">Report prefix, e.g. "land".</param>
/// <param name="GroupHeader">Bytes before each group (tiledata and hues: 4).</param>
/// <param name="RecordsPerGroup">Records per group (1 for an ungrouped table).</param>
/// <param name="RecordSize">Bytes per record.</param>
/// <param name="Groups">Number of groups, or null for "as many as fit in the rest of the file".</param>
/// <param name="NameOffset">Offset of a NUL-terminated fixed-size name inside the record, or -1.</param>
/// <param name="NameLength">Size of that name field.</param>
public sealed record RecordSection( string Name, int GroupHeader, int RecordsPerGroup, int RecordSize, int? Groups = null, int NameOffset = -1, int NameLength = 0 )
{
    public int GroupSize => GroupHeader + RecordsPerGroup * RecordSize;
}

/// <summary>Layout of a flat MUL table made of <see cref="RecordSection" />s.</summary>
public sealed record RecordFileSpec( string Asset, string FileName, IReadOnlyList<RecordSection> Sections )
{
    /// <summary>tiledata.mul (High Seas+ 64-bit flags): 0x4000 land then item records, groups of 32.</summary>
    public static RecordFileSpec TileData => new( "tiledata", "tiledata.mul",
    [
        new RecordSection( "land", 4, 32, 30, 512, NameOffset: 10, NameLength: 20 ),
        new RecordSection( "item", 4, 32, 41, NameOffset: 21, NameLength: 20 )
    ] );

    /// <summary>hues.mul: groups of 8 hues (32 colors, table start/end, 20-byte name).</summary>
    public static RecordFileSpec Hues => new( "hues", "hues.mul", [new RecordSection( "hue", 4, 8, 88, NameOffset: 68, NameLength: 20 )] );

    /// <summary>animdata.mul: groups of 8 item animations (64 frame offsets, unknown, count, interval, start).</summary>
    public static RecordFileSpec AnimData => new( "animdata", "animdata.mul", [new RecordSection( "anim", 4, 8, 68 )] );

    /// <summary>radarcol.mul: one ARGB1555 color per land then item id.</summary>
    public static RecordFileSpec RadarColors => new( "radarcol", "radarcol.mul", [new RecordSection( "color", 0, 1, 2 )] );

    /// <summary>Byte ranges of every record of a file of <paramref name="length" /> bytes.</summary>
    public IEnumerable<(string Key, RecordSection Section, int Offset, int Index)> Records( long length )
    {
        long pos = 0;

        foreach ( var section in Sections )
        {
            var groups = section.Groups ?? ( int )( ( length - pos ) / section.GroupSize );
            var index = 0;

            for ( var g = 0; g < groups && pos + section.GroupSize <= length; g++ )
            {
                pos += section.GroupHeader;

                for ( var r = 0; r < section.RecordsPerGroup; r++, index++ )
                {
                    yield return ( $"{section.Name} 0x{index:X4}", section, ( int )pos, index );
                    pos += section.RecordSize;
                }
            }
        }
    }
}

/// <summary>
///     Merges flat record tables (tiledata, hues, radarcol). The output starts as upstream's file
///     (so group headers and trailing bytes are upstream's) and custom local records are written
///     back over it. Names are compared up to their NUL, since the client ignores what follows it.
/// </summary>
public static class RecordFileMerger
{
    public static AssetMergeResult Merge( RecordFileSpec spec, UOFileSet local, UOFileSet upstream, IReadOnlyList<UOFileSet> stock, string? outputPath )
    {
        var result = new AssetMergeResult( spec.Asset );
        var localData = File.ReadAllBytes( local.Find( spec.FileName ) ?? throw new FileNotFoundException( $"{spec.FileName} is missing from {local.Root}." ) );
        var upstreamData = File.ReadAllBytes( upstream.Find( spec.FileName ) ?? throw new FileNotFoundException( $"{spec.FileName} is missing from {upstream.Root}." ) );
        var stockData = stock.Select( s => s.Find( spec.FileName ) ).OfType<string>().Select( File.ReadAllBytes ).ToList();

        // Local may have grown (custom ids past upstream's end); keep its extra records.
        var output = new byte[Math.Max( localData.Length, upstreamData.Length )];
        upstreamData.CopyTo( output, 0 );

        if ( localData.Length > upstreamData.Length )
        {
            localData.AsSpan( upstreamData.Length ).CopyTo( output.AsSpan( upstreamData.Length ) );
        }

        var upstreamRecords = spec.Records( upstreamData.Length ).Select( r => r.Offset ).ToHashSet();
        var localFiller = Fillers( spec, localData );
        var upstreamFiller = Fillers( spec, upstreamData );
        var index = 0;

        foreach ( var (key, section, offset, id) in spec.Records( localData.Length ) )
        {
            var size = section.RecordSize;
            var l = localData.AsSpan( offset, size );
            var hasUpstream = upstreamRecords.Contains( offset );
            var u = hasUpstream ? upstreamData.AsSpan( offset, size ) : [];
            var localRecord = l.ToArray();

            bool IsStock()
            {
                foreach ( var s in stockData )
                {
                    if ( offset + size <= s.Length && Equal( section, localRecord, s.AsSpan( offset, size ) ) )
                    {
                        return true;
                    }
                }

                return false;
            }

            // Unused slots (typically ids an older client did not define yet) are not custom content:
            // all-zero records, or the filler record a section repeats for its unused ids.
            var localUsed = IsUsed( l, localFiller.GetValueOrDefault( section ) );
            var upstreamUsed = hasUpstream && IsUsed( u, upstreamFiller.GetValueOrDefault( section ) );
            var action = localUsed || upstreamUsed
                ? MergeRules.Decide( localUsed, upstreamUsed, localUsed && upstreamUsed && Equal( section, l, u ), IsStock )
                : MergeAction.Unchanged;
            // Upstream blanked a slot local still has official (stock) data in: stale, take upstream.
            if ( action == MergeAction.KeptLocalOnly && hasUpstream && IsStock() )
            {
                action = MergeAction.Updated;
            }

            result.Record( key, action, action == MergeAction.KeptLocalOnly && IsStock(), id );

            if ( action.TakesLocal() )
            {
                l.CopyTo( output.AsSpan( offset ) );
            }

            index++;
        }

        // Records only upstream has.
        foreach ( var (key, _, _, id) in spec.Records( upstreamData.Length ).Skip( index ) )
        {
            result.Record( key, MergeAction.Added, id: id );
        }

        if ( outputPath is not null )
        {
            UOPMerger.WriteAtomically( outputPath, path => File.WriteAllBytes( path, output ) );
            result.OutputPath = outputPath;
        }

        return result;
    }

    private static bool IsUsed( ReadOnlySpan<byte> record, byte[]? filler ) =>
        record.ContainsAnyExcept( ( byte )0 ) && ( filler is null || !record.SequenceEqual( filler ) );

    /// <summary>
    ///     Per section, the record value that fills unused slots: the most common record, if it makes up
    ///     at least 2% of the section (hues.mul repeats a 0x0001 ramp, radarcol.mul the color 0x0421).
    /// </summary>
    private static Dictionary<RecordSection, byte[]> Fillers( RecordFileSpec spec, byte[] data )
    {
        var fillers = new Dictionary<RecordSection, byte[]>();

        foreach ( var group in spec.Records( data.Length ).GroupBy( r => r.Section ) )
        {
            var counts = new Dictionary<UInt128, (int Count, int Offset)>();
            var total = 0;

            foreach ( var r in group )
            {
                var f = Fingerprint.Of( data.AsSpan( r.Offset, r.Section.RecordSize ) );
                counts[f] = counts.TryGetValue( f, out var c ) ? ( c.Count + 1, c.Offset ) : ( 1, r.Offset );
                total++;
            }

            var top = counts.Values.MaxBy( c => c.Count );

            if ( top.Count >= 16 && top.Count * 50 >= total )
            {
                fillers[group.Key] = data.AsSpan( top.Offset, group.Key.RecordSize ).ToArray();
            }
        }

        return fillers;
    }

    private static bool Equal( RecordSection section, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b )
    {
        if ( a.SequenceEqual( b ) )
        {
            return true;
        }

        if ( section.NameOffset < 0 )
        {
            return false;
        }

        var nameEnd = section.NameOffset + section.NameLength;

        return a[..section.NameOffset].SequenceEqual( b[..section.NameOffset] ) &&
               a[nameEnd..].SequenceEqual( b[nameEnd..] ) &&
               UntilNul( a.Slice( section.NameOffset, section.NameLength ) ).SequenceEqual( UntilNul( b.Slice( section.NameOffset, section.NameLength ) ) );

        static ReadOnlySpan<byte> UntilNul( ReadOnlySpan<byte> s ) => s.IndexOf( ( byte )0 ) is >= 0 and var i ? s[..i] : s;
    }
}
