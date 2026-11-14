using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.IO;
using UODataAccess.Loaders;
using UODataAccess.Merge;

namespace UOMerge;

/// <summary>
///     Updates a customised client folder from a newer official one without losing custom work.
///     Never writes to the input folders: merged files go to <c>--out</c>, one file per asset,
///     each written as soon as that asset is done.
/// </summary>
public static class Program
{
    private const string Usage =
        """
        uomerge --local DIR --upstream DIR --out DIR [--stock DIR]... [--assets LIST] [--dry-run]

          --local     customised client to update (read only)
          --upstream  newer official client to take updates from (read only)
          --stock     other official clients: local entries matching any of them are stale stock
                      and get upstream's version; anything else counts as custom and is kept
          --out       folder that receives the merged files, merge-report.txt, merge-items.tsv
                      and review/index.html
          --assets    comma list (default: gumps,art,cliloc,tiledata,hues,radarcol)
                      gumps, art, sounds, cliloc, tiledata, hues, radarcol, animdata, textures,
                      anims (anim*.idx/mul), uop:<file.uop> (merge any UOP by hash),
                      copy:<file> (take upstream's file as-is, e.g. files local lacks)
          --dry-run   report only, write no client files
        """;

    public static int Main( string[] args )
    {
        string? local = null, upstream = null, output = null;
        var stock = new List<string>();
        var assets = "gumps,art,cliloc,tiledata,hues,radarcol";
        var dryRun = false;

        for ( var i = 0; i < args.Length; i++ )
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException( $"{args[i]} needs a value." );

            switch ( args[i] )
            {
                case "--local": local = Next(); break;
                case "--upstream": upstream = Next(); break;
                case "--stock": stock.Add( Next() ); break;
                case "--out": output = Next(); break;
                case "--assets": assets = Next(); break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine( Usage );
                    return 2;
            }
        }

        if ( local is null || upstream is null || output is null )
        {
            Console.Error.WriteLine( Usage );
            return 2;
        }

        var outFull = Path.GetFullPath( output );

        foreach ( var input in stock.Append( local ).Append( upstream ) )
        {
            if ( outFull.StartsWith( Path.GetFullPath( input ).TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ||
                 string.Equals( outFull.TrimEnd( Path.DirectorySeparatorChar ), Path.GetFullPath( input ).TrimEnd( Path.DirectorySeparatorChar ), StringComparison.OrdinalIgnoreCase ) )
            {
                Console.Error.WriteLine( $"--out must not be inside an input folder ({input})." );
                return 2;
            }
        }

        Directory.CreateDirectory( outFull );
        var localFiles = new UOFileSet( local );
        var upstreamFiles = new UOFileSet( upstream );
        var stockFiles = stock.Select( s => new UOFileSet( s ) ).ToList();

        // Upstream is stock too: a local entry equal to it is unchanged anyway, but this keeps the rule simple.
        var stockAll = stockFiles.Prepend( upstreamFiles ).ToList();
        var results = new List<AssetMergeResult>();
        var notes = new List<string>();

        foreach ( var asset in assets.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
        {
            foreach ( var job in Jobs( asset, localFiles, upstreamFiles ) )
            {
                var localPath = localFiles.Find( job.FileName );
                var upstreamPath = upstreamFiles.Find( job.FileName );

                if ( ( job.RequiresLocal && localPath is null ) || upstreamPath is null )
                {
                    notes.Add( $"{job.FileName}: skipped, missing from {( localPath is null ? "local" : "upstream" )}." );
                    Console.WriteLine( notes[^1] );
                    continue;
                }

                if ( localPath is not null && SameFile( localPath, upstreamPath ) )
                {
                    notes.Add( $"{job.FileName}: identical, nothing to do." );
                    Console.WriteLine( notes[^1] );
                    continue;
                }

                var target = dryRun ? null : Path.Combine( outFull, Path.GetFileName( localPath ?? upstreamPath ) );
                var clock = Stopwatch.StartNew();
                Console.WriteLine( $"{job.FileName}: merging..." );
                var result = job.Run( localFiles, upstreamFiles, stockAll, target );

                // Nothing custom survived: ship upstream's file verbatim rather than a re-packed equivalent.
                // Not when that would change how the file is stored (BWT cliloc, transcoded UOP entries).
                if ( target is not null && result.IsPureUpstream && job.RequiresLocal && job.AllowVerbatim && result.Transcoded == 0 )
                {
                    File.Copy( upstreamPath, target, true );

                    // An idx must match its mul: replace the merged one too.
                    if ( job.Companion is not null && upstreamFiles.Find( job.Companion ) is { } companion )
                    {
                        File.Copy( companion, Path.Combine( outFull, Path.GetFileName( localFiles.Find( job.Companion ) ?? companion ) ), true );
                    }

                    notes.Add( $"{job.FileName}: no custom entries, copied upstream's file as-is." );
                }

                results.Add( result );
                Console.WriteLine( $"  {result} ({clock.Elapsed.TotalSeconds:F1}s){( target is null ? "" : $" -> {target}" )}" );
            }
        }

        WriteReports( outFull, results, notes, localFiles, upstreamFiles, local, upstream, stock, dryRun );
        Console.WriteLine( $"Report: {Path.Combine( outFull, "merge-report.txt" )}" );
        return 0;
    }

    private sealed record Job( string FileName, Func<UOFileSet, UOFileSet, IReadOnlyList<UOFileSet>, string?, AssetMergeResult> Run, bool RequiresLocal = true, string? Companion = null, bool AllowVerbatim = true );

    /// <summary>Takes upstream's file wholesale (new files such as AnimationFrame6.uop).</summary>
    private static AssetMergeResult CopyUpstream( UOFileSet upstream, string file, string? output )
    {
        var result = new AssetMergeResult( file );
        result.Record( file, MergeAction.Added );

        if ( output is not null )
        {
            UOPMerger.WriteAtomically( output, path => File.Copy( upstream.Find( file )!, path, true ) );
            result.OutputPath = output;
        }

        return result;
    }

    private static IEnumerable<Job> Jobs( string asset, UOFileSet local, UOFileSet upstream )
    {
        switch ( asset.ToLowerInvariant() )
        {
            case "gumps": yield return Uop( UOPMergeSpec.Gumps ); break;
            case "art": yield return Uop( UOPMergeSpec.Art ); break;
            case "sounds": yield return Uop( UOPMergeSpec.Sounds ); break;
            case "tiledata": yield return Records( RecordFileSpec.TileData ); break;
            case "hues": yield return Records( RecordFileSpec.Hues ); break;
            case "radarcol": yield return Records( RecordFileSpec.RadarColors ); break;
            case "animdata": yield return Records( RecordFileSpec.AnimData ); break;
            case "textures": yield return IndexedMUL( IndexedMULSpec.Textures ); break;
            case "anims":
                for ( var file = 1; file <= 6; file++ )
                {
                    var spec = IndexedMULSpec.Animations( file );

                    if ( local.Exists( spec.IdxName ) )
                    {
                        yield return IndexedMUL( spec );
                    }
                }

                break;
            case "cliloc":
                foreach ( var language in Cliloc.AvailableLanguages( local ).Intersect( Cliloc.AvailableLanguages( upstream ) ) )
                {
                    yield return new Job( $"Cliloc.{language}", ( l, u, s, o ) => ClilocMerger.Merge( language, l, u, s, o ), AllowVerbatim: false );
                }

                break;
            default:
                if ( asset.StartsWith( "copy:", StringComparison.OrdinalIgnoreCase ) )
                {
                    var file = asset[5..];
                    yield return new Job( file, ( _, u, _, o ) => CopyUpstream( u, file, o ), RequiresLocal: false );
                    break;
                }

                if ( asset.StartsWith( "uop:", StringComparison.OrdinalIgnoreCase ) )
                {
                    var file = asset[4..];
                    yield return Uop( new UOPMergeSpec( Path.GetFileNameWithoutExtension( file ), file ) );
                    break;
                }

                throw new ArgumentException( $"Unknown asset '{asset}'." );
        }

        static Job Uop( UOPMergeSpec spec ) => new( spec.FileName, ( l, u, s, o ) => UOPMerger.Merge( spec, l, u, s, o ) );

        static Job Records( RecordFileSpec spec ) => new( spec.FileName, ( l, u, s, o ) => RecordFileMerger.Merge( spec, l, u, s, o ) );

        // The output path names the .mul; its idx goes next to it under the local file's name.
        static Job IndexedMUL( IndexedMULSpec spec ) => new( spec.MULName, ( l, u, s, o ) =>
            IndexedMULMerger.Merge( spec, l, u, s, o, o is null ? null : Path.Combine( Path.GetDirectoryName( o )!, Path.GetFileName( l.Find( spec.IdxName ) ?? spec.IdxName ) ) ), Companion: spec.IdxName );
    }

    private static bool SameFile( string a, string b )
    {
        if ( new FileInfo( a ).Length != new FileInfo( b ).Length )
        {
            return false;
        }

        using var fa = File.OpenRead( a );
        using var fb = File.OpenRead( b );
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];

        while ( true )
        {
            var na = fa.ReadAtLeast( ba, ba.Length, false );
            var nb = fb.ReadAtLeast( bb, bb.Length, false );

            if ( na != nb || !ba.AsSpan( 0, na ).SequenceEqual( bb.AsSpan( 0, nb ) ) )
            {
                return false;
            }

            if ( na == 0 )
            {
                return true;
            }
        }
    }

    private static void WriteReports( string outDir, List<AssetMergeResult> results, List<string> notes, UOFileSet localFiles, UOFileSet upstreamFiles,
        string local, string upstream, List<string> stock, bool dryRun )
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine( inv, $"Merge {( dryRun ? "(dry run) " : "" )}{DateTime.Now:yyyy-MM-dd HH:mm}" );
        text.AppendLine( inv, $"  local:    {local}" );
        text.AppendLine( inv, $"  upstream: {upstream}" );

        foreach ( var s in stock )
        {
            text.AppendLine( inv, $"  stock:    {s}" );
        }

        text.AppendLine();

        foreach ( var r in results )
        {
            text.AppendLine( r.ToString() );
        }

        text.AppendLine();

        foreach ( var n in notes )
        {
            text.AppendLine( n );
        }

        text.AppendLine();
        text.AppendLine( "Kept custom entries (local differs from every stock client):" );

        foreach ( var r in results )
        {
            var custom = r.Items.Where( i => i.Action == MergeAction.KeptCustom || ( i.Action == MergeAction.KeptLocalOnly && !i.LocalIsStock ) ).ToList();

            if ( custom.Count > 0 )
            {
                text.AppendLine( inv, $"  {r.Asset} ({custom.Count}): {string.Join( ", ", custom.Take( 400 ).Select( i => i.Key ) )}{( custom.Count > 400 ? ", ..." : "" )}" );
            }
        }

        File.WriteAllText( Path.Combine( outDir, "merge-report.txt" ), text.ToString() );

        var tsv = new StringBuilder( "asset\tkey\taction\tlocal_is_stock\n" );

        foreach ( var r in results )
        {
            foreach ( var i in r.Items )
            {
                tsv.Append( inv, $"{r.Asset}\t{i.Key}\t{i.Action}\t{( i.Action == MergeAction.KeptLocalOnly ? i.LocalIsStock.ToString( inv ) : "" )}\n" );
            }
        }

        File.WriteAllText( Path.Combine( outDir, "merge-items.tsv" ), tsv.ToString() );
        WriteReview( outDir, results, localFiles, upstreamFiles );
    }

    /// <summary>HTML page listing every kept custom entry next to what upstream has, to eyeball the decisions.</summary>
    private static void WriteReview( string outDir, List<AssetMergeResult> results, UOFileSet localFiles, UOFileSet upstreamFiles )
    {
        var review = Path.Combine( outDir, "review" );
        Directory.CreateDirectory( review );
        using var localData = new UOData( localFiles.Root );
        using var upstreamData = new UOData( upstreamFiles.Root );
        var html = new StringBuilder( "<!doctype html><meta charset=utf-8><title>Merge review</title><style>body{font:13px sans-serif;background:#222;color:#ddd}" +
                                      "table{border-collapse:collapse}td,th{border:1px solid #444;padding:4px;vertical-align:top}img{image-rendering:pixelated;max-width:400px;background:#000}" +
                                      ".t{white-space:pre-wrap;max-width:600px}</style><h1>Kept custom entries</h1>" );

        foreach ( var r in results )
        {
            var items = r.Items.Where( i => i.Action == MergeAction.KeptCustom || ( i.Action == MergeAction.KeptLocalOnly && !i.LocalIsStock ) ).ToList();

            if ( items.Count == 0 )
            {
                continue;
            }

            html.Append( CultureInfo.InvariantCulture, $"<h2>{WebUtility.HtmlEncode( r.Asset )} ({items.Count})</h2><table><tr><th>id</th><th>action</th><th>kept (local)</th><th>upstream (discarded)</th></tr>" );

            foreach ( var item in items )
            {
                var (localCell, upstreamCell) = r.Asset switch
                {
                    "gumps" => ( Image( review, $"gump-{item.Id:X4}-local", Safe( () => localData.Gumps.Get( item.Id ) ) ),
                        Image( review, $"gump-{item.Id:X4}-upstream", Safe( () => upstreamData.Gumps.Get( item.Id ) ) ) ),
                    "art" => ( Image( review, $"art-{item.Id:X5}-local", Safe( () => ArtImage( localData, item.Id ) ) ),
                        Image( review, $"art-{item.Id:X5}-upstream", Safe( () => ArtImage( upstreamData, item.Id ) ) ) ),
                    _ when r.Asset.StartsWith( "cliloc.", StringComparison.Ordinal ) => ( ClilocText( localFiles, r.Asset[7..], item.Id ), ClilocText( upstreamFiles, r.Asset[7..], item.Id ) ),
                    _ => ( "", "" )
                };

                html.Append( CultureInfo.InvariantCulture, $"<tr><td>{WebUtility.HtmlEncode( item.Key )}</td><td>{item.Action}</td><td>{localCell}</td><td>{upstreamCell}</td></tr>" );
            }

            html.Append( "</table>" );
        }

        File.WriteAllText( Path.Combine( review, "index.html" ), html.ToString() );
    }

    private static Pixmap? Safe( Func<Pixmap?> load )
    {
        try
        {
            return load();
        }
        catch ( Exception e ) when ( e is IOException or InvalidDataException )
        {
            return null;
        }
    }

    private static Pixmap? ArtImage( UOData data, int id ) => id < Art.LandCount ? data.Art.GetLand( id ) : data.Art.GetItem( id - Art.LandCount );

    private static string Image( string dir, string name, Pixmap? pixmap )
    {
        if ( pixmap is null )
        {
            return "<i>none</i>";
        }

        using ( var stream = File.Create( Path.Combine( dir, name + ".png" ) ) )
        {
            PngEncoder.Write( pixmap, stream );
        }

        return $"<img src=\"{name}.png\" title=\"{pixmap.Width}x{pixmap.Height}\"><br>{pixmap.Width}x{pixmap.Height}";
    }

    private static string ClilocText( UOFileSet files, string language, int number ) =>
        Cliloc.Get( files, language ).GetString( number ) is { } s ? $"<div class=t>{WebUtility.HtmlEncode( s )}</div>" : "<i>none</i>";
}
