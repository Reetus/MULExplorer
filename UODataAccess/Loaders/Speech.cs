using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     A speech keyword. Several entries share an id (one per language/variant). A leading or
///     trailing <c>*</c> lets the keyword match anywhere rather than only at the start/end of a phrase.
/// </summary>
public sealed record SpeechEntry( int Order, int Id, string Keyword )
{
    public string[] Parts { get; } = Keyword.Split( '*', StringSplitOptions.RemoveEmptyEntries );

    /// <summary>Keyword starts with <c>*</c>: the phrase need not start with it.</summary>
    public bool AnyStart => Keyword.Length > 0 && Keyword[0] == '*';

    /// <summary>Keyword ends with <c>*</c>: the phrase need not end with it.</summary>
    public bool AnyEnd => Keyword.Length > 0 && Keyword[^1] == '*';

    /// <summary>The client's keyword test (ported from ClassicUO <c>SpeechesLoader.IsMatch</c>).</summary>
    public bool Matches( string input )
    {
        foreach ( var part in Parts )
        {
            if ( part.Length == 0 || part.Length > input.Length )
            {
                continue;
            }

            if ( !AnyStart && !input.StartsWith( part, StringComparison.InvariantCultureIgnoreCase ) )
            {
                continue;
            }

            if ( !AnyEnd && !input.EndsWith( part, StringComparison.InvariantCultureIgnoreCase ) )
            {
                continue;
            }

            var idx = input.IndexOf( part, StringComparison.InvariantCultureIgnoreCase );

            while ( idx >= 0 )
            {
                var end = idx + part.Length;

                // A whole word: "bank", " bank ", "!bank" but not "banker".
                if ( ( idx == 0 || !char.IsLetter( input[idx - 1] ) ) && ( end >= input.Length || !char.IsLetter( input[end] ) ) )
                {
                    return true;
                }

                idx = input.IndexOf( part, idx + 1, StringComparison.InvariantCultureIgnoreCase );
            }
        }

        return false;
    }
}

/// <summary>
///     <c>speech.mul</c>: big-endian <c>(id:u16, length:u16, utf8[length])</c> records. Empty records
///     are skipped, as in ClassicUO.
/// </summary>
public sealed class Speech
{
    public Speech( UOFileSet files )
    {
        var path = files.Find( "speech.mul" ) ?? throw new FileNotFoundException( "speech.mul not found." );
        Entries = Parse( File.ReadAllBytes( path ) );
    }

    /// <summary>Entries in file order.</summary>
    public IReadOnlyList<SpeechEntry> Entries { get; }

    public int Count => Entries.Count;

    public static List<SpeechEntry> Parse( ReadOnlySpan<byte> data )
    {
        var list = new List<SpeechEntry>( data.Length / 16 );
        var pos = 0;

        while ( pos + 4 <= data.Length )
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian( data[pos..] );
            var length = BinaryPrimitives.ReadUInt16BigEndian( data[( pos + 2 )..] );
            pos += 4;
            length = ( ushort )Math.Min( length, data.Length - pos );

            if ( length > 0 )
            {
                list.Add( new SpeechEntry( list.Count, id, string.Intern( Encoding.UTF8.GetString( data.Slice( pos, length ) ) ) ) );
            }

            pos += length;
        }

        return list;
    }

    /// <summary>Entries whose keyword the client would detect in <paramref name="phrase" />, sorted by id.</summary>
    public IReadOnlyList<SpeechEntry> GetKeywords( string phrase )
    {
        var text = phrase.Trim( ' ' );
        return Entries.Where( e => e.Matches( text ) ).OrderBy( e => e.Id ).ToList();
    }
}
