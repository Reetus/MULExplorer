using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>A skill: <see cref="IsAction" /> skills get a use button in the client's skill gump.</summary>
public readonly record struct SkillInfo( int Index, string Name, bool IsAction, int Extra );

/// <summary>
///     <c>Skills.idx</c>+<c>skills.mul</c>: per entry a byte "has action" followed by the
///     null-terminated ASCII name. Like ClassicUO, empty index slots are skipped and skills are
///     numbered consecutively.
/// </summary>
public sealed class Skills
{
    public Skills( UOFileSet files )
    {
        using var index = FileIndex.Open( files, "Skills.idx", "skills.mul" )
                          ?? throw new FileNotFoundException( "Skills.idx/skills.mul not found." );

        var list = new List<SkillInfo>();

        for ( var i = 0; i < index.Length; i++ )
        {
            if ( index.TryRead( i, out var data, out var entry ) && data.Length > 0 )
            {
                list.Add( new SkillInfo( list.Count, ReadName( data[1..] ), data[0] != 0, entry.Extra ) );
            }
        }

        Entries = list;
    }

    public IReadOnlyList<SkillInfo> Entries { get; }

    public int Count => Entries.Count;

    public SkillInfo? Get( int index ) => ( uint )index < ( uint )Entries.Count ? Entries[index] : null;

    private static string ReadName( ReadOnlySpan<byte> bytes )
    {
        var end = bytes.IndexOf( ( byte )0 );
        return Encoding.ASCII.GetString( end < 0 ? bytes : bytes[..end] );
    }
}
