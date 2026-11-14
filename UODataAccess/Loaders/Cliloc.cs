using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

public readonly record struct ClilocEntry( int Number, string Text, byte Flag );

/// <summary>
///     Localized strings (<c>Cliloc.enu</c> etc.): a 6-byte header then
///     <c>(number:i32, flag:u8, length:u16, utf8)</c> records. Clients from 7.0.104 store the file
///     BWT-compressed.
/// </summary>
public sealed class Cliloc
{
    private readonly Dictionary<int, ClilocEntry> _entries;

    private Cliloc( string language, List<ClilocEntry> entries )
    {
        Language = language;
        Entries = entries;
        _entries = entries.ToDictionary( e => e.Number );
    }

    public string Language { get; }

    /// <summary>Entries in file order (ascending number).</summary>
    public IReadOnlyList<ClilocEntry> Entries { get; }

    public int Count => Entries.Count;

    /// <summary>Languages available in the folder, from <c>Cliloc.xxx</c> file extensions.</summary>
    public static IReadOnlyList<string> AvailableLanguages( UOFileSet files ) =>
        files.FileNames
            .Where( n => n.StartsWith( "cliloc.", StringComparison.OrdinalIgnoreCase ) && n.Length == 10 )
            .Select( n => n[7..].ToLowerInvariant() )
            .Order()
            .ToList();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UOFileSet, System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Cliloc>>> Cache = new();

    /// <summary>Loads a language once per client folder and shares it (UI and MCP switch languages freely).</summary>
    public static Cliloc Get( UOFileSet files, string language )
    {
        var lang = language.Trim().ToLowerInvariant();
        var lazy = Cache.GetOrCreateValue( files ).GetOrAdd( lang, l => new Lazy<Cliloc>( () => Load( files, l ) ) );

        try
        {
            return lazy.Value;
        }
        catch
        {
            Cache.GetOrCreateValue( files ).TryRemove( lang, out _ );
            throw;
        }
    }

    public static Cliloc Load( UOFileSet files, string language = "enu" )
    {
        var path = files.Find( $"Cliloc.{language}" ) ?? throw new FileNotFoundException( $"Cliloc.{language} not found." );
        return Load( path, language );
    }

    public static Cliloc Load( string path, string language )
    {
        var bytes = File.ReadAllBytes( path );
        var entries = TryParse( bytes );

        if ( entries is null )
        {
            entries = TryParse( BWTDecompress.Decompress( bytes ) ) ?? throw new InvalidDataException( $"Unrecognised cliloc format: {path}" );
        }

        return new Cliloc( language, entries );
    }

    public string? GetString( int number ) => _entries.TryGetValue( number, out var e ) ? e.Text : null;

    public bool TryGet( int number, out ClilocEntry entry ) => _entries.TryGetValue( number, out entry );

    /// <summary>Position of <paramref name="number" /> in <see cref="Entries" />, or <c>~insertionPoint</c> if absent.</summary>
    public int IndexOf( int number )
    {
        int lo = 0, hi = Entries.Count - 1;

        while ( lo <= hi )
        {
            var mid = ( lo + hi ) >>> 1;
            var n = Entries[mid].Number;

            if ( n == number )
            {
                return mid;
            }

            if ( n < number )
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    /// <summary>
    ///     Entries whose text contains <paramref name="text" /> (case-insensitive) or matches it as a
    ///     regular expression, or whose number equals it when it parses as one.
    /// </summary>
    public IEnumerable<ClilocEntry> Search( string text, bool regex = false )
    {
        if ( string.IsNullOrEmpty( text ) )
        {
            return Entries;
        }

        if ( regex )
        {
            var r = new System.Text.RegularExpressions.Regex( text, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 2 ) );
            return Entries.Where( e => r.IsMatch( e.Text ) );
        }

        var isNumber = int.TryParse( text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number );
        return Entries.Where( e => ( isNumber && e.Number == number ) || e.Text.Contains( text, StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>Fills <c>~1_NAME~</c> style placeholders from a tab-separated argument string.</summary>
    public string? Format( int number, string? args )
    {
        var text = GetString( number );

        if ( text is null || string.IsNullOrEmpty( args ) )
        {
            return text;
        }

        var parts = args.Split( '\t' );
        var sb = new StringBuilder();
        var i = 0;

        while ( i < text.Length )
        {
            var start = text.IndexOf( '~', i );
            var end = start < 0 ? -1 : text.IndexOf( '~', start + 1 );

            if ( end < 0 )
            {
                sb.Append( text.AsSpan( i ) );
                break;
            }

            sb.Append( text.AsSpan( i, start - i ) );
            var token = text.AsSpan( start + 1, end - start - 1 );
            var underscore = token.IndexOf( '_' );

            if ( int.TryParse( underscore < 0 ? token : token[..underscore], out var n ) && n >= 1 && n <= parts.Length )
            {
                sb.Append( parts[n - 1] );
            }
            else
            {
                sb.Append( text.AsSpan( start, end - start + 1 ) );
            }

            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Writes an uncompressed cliloc: header <c>(2:i32, 1:i16)</c> then the records in ascending
    ///     number order. ClassicUO-based clients detect BWT by the first bytes, so they read either
    ///     form; the official client from 7.0.104 expects the compressed one.
    /// </summary>
    public static void Write( Stream stream, IEnumerable<ClilocEntry> entries )
    {
        Span<byte> header = stackalloc byte[7];
        BinaryPrimitives.WriteInt32LittleEndian( header, 2 );
        BinaryPrimitives.WriteInt16LittleEndian( header[4..], 1 );
        stream.Write( header[..6] );

        var buffer = new byte[256];
        var last = int.MinValue;

        foreach ( var e in entries.OrderBy( e => e.Number ) )
        {
            if ( e.Number == last )
            {
                throw new ArgumentException( $"Duplicate cliloc number {e.Number}.", nameof( entries ) );
            }

            var length = Encoding.UTF8.GetByteCount( e.Text );

            if ( length > ushort.MaxValue )
            {
                throw new ArgumentException( $"Cliloc {e.Number} is longer than 65535 bytes.", nameof( entries ) );
            }

            if ( buffer.Length < length )
            {
                buffer = new byte[Math.Max( length, buffer.Length * 2 )];
            }

            BinaryPrimitives.WriteInt32LittleEndian( header, e.Number );
            header[4] = e.Flag;
            BinaryPrimitives.WriteUInt16LittleEndian( header[5..], ( ushort )length );
            stream.Write( header );
            stream.Write( buffer, 0, Encoding.UTF8.GetBytes( e.Text, buffer ) );
            last = e.Number;
        }
    }

    private static List<ClilocEntry>? TryParse( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 6 )
        {
            return null;
        }

        var list = new List<ClilocEntry>( data.Length / 32 );
        var pos = 6;
        var last = -1;

        while ( pos + 7 <= data.Length )
        {
            var number = BinaryPrimitives.ReadInt32LittleEndian( data[pos..] );
            var flag = data[pos + 4];
            var length = BinaryPrimitives.ReadUInt16LittleEndian( data[( pos + 5 )..] );
            pos += 7;

            // Numbers are ascending; anything else means we are reading compressed bytes.
            if ( number <= last || pos + length > data.Length )
            {
                return null;
            }

            list.Add( new ClilocEntry( number, Encoding.UTF8.GetString( data.Slice( pos, length ) ), flag ) );
            last = number;
            pos += length;
        }

        return pos == data.Length && list.Count > 0 ? list : null;
    }
}
