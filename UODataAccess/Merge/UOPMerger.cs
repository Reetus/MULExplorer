using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Merge;

/// <summary>Content fingerprint of a decompressed UOP entry; <paramref name="id" /> is -1 when the name is unknown.</summary>
public delegate UInt128 EntryFingerprint( int id, ReadOnlySpan<byte> data );

/// <summary>How to merge one UOP container.</summary>
/// <param name="Asset">Report name, e.g. "gumps".</param>
/// <param name="FileName">Container file name, e.g. <c>gumpartLegacyMUL.uop</c>.</param>
public sealed record UOPMergeSpec( string Asset, string FileName )
{
    /// <summary>Entry name formats used to turn hashes back into ids for the report.</summary>
    public IReadOnlyList<string> NamePatterns { get; init; } = [];

    /// <summary>Ids probed with <see cref="NamePatterns" />.</summary>
    public int MaxId { get; init; }

    /// <summary>
    ///     Decides when two differing payloads are the same asset (e.g. an image re-encoded by an
    ///     editor). Defaults to the decompressed bytes.
    /// </summary>
    public EntryFingerprint? Fingerprint { get; init; }

    /// <summary>
    ///     Store upstream entries the way local stores its own (when local uses a single compression
    ///     that can be produced: none or zlib), so e.g. an uncompressed custom client does not end up
    ///     with BWT entries. The output container also takes local's shape.
    /// </summary>
    public bool MatchLocalCompression { get; init; } = true;

    public static UOPMergeSpec Gumps => new( "gumps", "gumpartLegacyMUL.uop" )
    {
        // Gump 9834 has shipped with a 7-digit name since at least 7.0.59.
        NamePatterns = ["build/gumpartlegacymul/{0:D8}.tga", "build/gumpartlegacymul/{0:D7}.tga"],
        MaxId = 0x10000,
        Fingerprint = static ( _, data ) =>
        {
            if ( data.Length >= 8 &&
                 Loaders.Gumps.Decode( data[8..], BinaryPrimitives.ReadInt32LittleEndian( data ), BinaryPrimitives.ReadInt32LittleEndian( data[4..] ) ) is { } pixmap )
            {
                return Merge.Fingerprint.Of( pixmap );
            }

            return Merge.Fingerprint.Of( data );
        }
    };

    public static UOPMergeSpec Art => new( "art", "artLegacyMUL.uop" )
    {
        NamePatterns = ["build/artlegacymul/{0:D8}.tga"],
        MaxId = 0x20000,
        Fingerprint = static ( id, data ) =>
        {
            var pixmap = id is >= 0 and < Loaders.Art.LandCount ? Loaders.Art.DecodeLand( data ) : Loaders.Art.DecodeStatic( data );
            return pixmap is null ? Merge.Fingerprint.Of( data ) : Merge.Fingerprint.Of( pixmap );
        }
    };

    public static UOPMergeSpec Sounds => new( "sounds", "soundLegacyMUL.uop" )
    {
        NamePatterns = ["build/soundlegacymul/{0:D8}.dat"],
        MaxId = 0x1000
    };

    /// <summary>Report key: art ids are shown as land/item ids.</summary>
    public string FormatKey( int id ) => Asset switch
    {
        "art" when id >= Loaders.Art.LandCount => $"item 0x{id - Loaders.Art.LandCount:X4}",
        "art" => $"land 0x{id:X4}",
        _ => $"0x{id:X4}"
    };
}

/// <summary>
///     Merges two UOP containers entry by entry (by hash, so any UOP works), copying the chosen
///     entries' stored bytes verbatim: nothing is re-encoded or recompressed.
/// </summary>
public static class UOPMerger
{
    public static AssetMergeResult Merge(
        UOPMergeSpec spec,
        UOFileSet local,
        UOFileSet upstream,
        IReadOnlyList<UOFileSet> stock,
        string? outputPath,
        CancellationToken cancellationToken = default )
    {
        var result = new AssetMergeResult( spec.Asset );
        var localPath = local.Find( spec.FileName );
        var upstreamPath = upstream.Find( spec.FileName );

        if ( localPath is null || upstreamPath is null )
        {
            throw new FileNotFoundException( $"{spec.FileName} is missing from {( localPath is null ? local.Root : upstream.Root )}." );
        }

        using var localArchive = UOPArchive.Open( localPath );
        using var upstreamArchive = UOPArchive.Open( upstreamPath );
        var stockArchives = stock.Select( s => s.Find( spec.FileName ) ).OfType<string>().Select( UOPArchive.Open ).ToList();

        try
        {
            var ids = BuildIdMap( spec );

            // Upstream order first, then entries only local has.
            var hashes = upstreamArchive.Entries.Keys.Concat( localArchive.Entries.Keys.Where( h => !upstreamArchive.Entries.ContainsKey( h ) ) ).ToArray();
            var decisions = new (MergeAction Action, bool LocalIsStock)[hashes.Length];

            Parallel.For( 0, hashes.Length, new ParallelOptions { CancellationToken = cancellationToken }, i =>
            {
                var hash = hashes[i];
                var id = ids.GetValueOrDefault( hash, -1 );
                var hasLocal = localArchive.TryGet( hash, out var l );
                var hasUpstream = upstreamArchive.TryGet( hash, out var u );
                UInt128? localPrint = null;

                UInt128 LocalPrint() => localPrint ??= Print( spec, id, localArchive.Read( l ) );

                bool IsStock()
                {
                    foreach ( var s in stockArchives )
                    {
                        if ( s.TryGet( hash, out var e ) && Print( spec, id, s.Read( e ) ) == LocalPrint() )
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var same = hasLocal && hasUpstream && Same( spec, id, localArchive, l, upstreamArchive, u, LocalPrint );
                var action = MergeRules.Decide( hasLocal, hasUpstream, same, IsStock );
                decisions[i] = ( action, action == MergeAction.KeptLocalOnly && IsStock() );
            } );

            for ( var i = 0; i < hashes.Length; i++ )
            {
                var id = ids.GetValueOrDefault( hashes[i], -1 );
                result.Record( id >= 0 ? spec.FormatKey( id ) : $"hash {hashes[i]:X16}", decisions[i].Action, decisions[i].LocalIsStock, id );
            }

            result.Items.Sort( static ( a, b ) => a.Id.CompareTo( b.Id ) );

            if ( outputPath is not null )
            {
                var target = spec.MatchLocalCompression ? LocalCompression( localArchive ) : null;

                WriteAtomically( outputPath, path =>
                {
                    using var writer = UOPArchiveWriter.Create( path, target is null ? upstreamArchive : localArchive );

                    for ( var i = 0; i < hashes.Length; i++ )
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fromLocal = decisions[i].Action.TakesLocal();
                        var archive = fromLocal ? localArchive : upstreamArchive;
                        archive.TryGet( hashes[i], out var entry );

                        if ( target is { } compression && entry.Compression != compression )
                        {
                            var plain = archive.Read( entry );
                            var stored = compression == UOPCompression.Zlib ? Deflate( plain ) : plain;
                            writer.Add( entry.Hash, archive.ReadHeader( entry ), stored, plain.Length, compression );
                            result.Transcoded++;
                        }
                        else
                        {
                            writer.CopyFrom( archive, entry );
                        }
                    }
                } );

                result.OutputPath = outputPath;
            }

            return result;
        }
        finally
        {
            foreach ( var archive in stockArchives )
            {
                archive.Dispose();
            }
        }
    }

    /// <summary>Writes through a temp file and renames, so a cancelled run never leaves a half-written asset.</summary>
    public static void WriteAtomically( string path, Action<string> write )
    {
        Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) )! );
        var temp = path + ".tmp";

        try
        {
            write( temp );
            File.Move( temp, path, true );
        }
        finally
        {
            if ( File.Exists( temp ) )
            {
                File.Delete( temp );
            }
        }
    }

    private static bool Same( UOPMergeSpec spec, int id, UOPArchive a, in UOPEntry ea, UOPArchive b, in UOPEntry eb, Func<UInt128> localPrint )
    {
        if ( ea.Compression == eb.Compression && ea.CompressedLength == eb.CompressedLength && a.ReadRaw( ea ).SequenceEqual( b.ReadRaw( eb ) ) )
        {
            return true;
        }

        var upstream = b.Read( eb );

        if ( ea.DecompressedLength == eb.DecompressedLength && a.Read( ea ).SequenceEqual( upstream ) )
        {
            return true;
        }

        return spec.Fingerprint is not null && localPrint() == Print( spec, id, upstream );
    }

    /// <summary>The single compression every local entry uses, if it is one we can write.</summary>
    private static UOPCompression? LocalCompression( UOPArchive local )
    {
        var kinds = local.Entries.Values.Select( e => e.Compression ).Distinct().ToList();
        return kinds is [var only and ( UOPCompression.None or UOPCompression.Zlib )] ? only : null;
    }

    private static byte[] Deflate( ReadOnlySpan<byte> data )
    {
        using var ms = new MemoryStream();

        using ( var zlib = new System.IO.Compression.ZLibStream( ms, System.IO.Compression.CompressionLevel.Optimal, true ) )
        {
            zlib.Write( data );
        }

        return ms.ToArray();
    }

    private static UInt128 Print( UOPMergeSpec spec, int id, ReadOnlySpan<byte> data ) =>
        spec.Fingerprint is { } f ? f( id, data ) : Fingerprint.Of( data );

    private static Dictionary<ulong, int> BuildIdMap( UOPMergeSpec spec )
    {
        var map = new Dictionary<ulong, int>();

        foreach ( var pattern in spec.NamePatterns )
        {
            var format = CompositeFormat.Parse( pattern );

            for ( var id = 0; id < spec.MaxId; id++ )
            {
                map.TryAdd( UOPArchive.Hash( format, id ), id );
            }
        }

        return map;
    }
}
