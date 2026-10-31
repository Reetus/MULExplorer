using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>A skill group and the indices of the skills in it.</summary>
public sealed record SkillGroup( int Index, string Name, IReadOnlyList<int> Skills );

/// <summary>
///     <c>skillgrp.mul</c>: <c>count:i32</c> (preceded by <c>-1</c> in the newer format, whose names
///     are UTF-16), then <c>count - 1</c> fixed-size names (17 bytes, or 34 for UTF-16; group 0 is
///     the implicit "Misc"), then one <c>i32</c> group index per skill.
/// </summary>
public sealed class SkillGroups
{
    /// <summary>Name of the implicit group 0.</summary>
    public const string MiscName = "Misc";

    public SkillGroups( UOFileSet files )
    {
        var path = files.Find( "skillgrp.mul" ) ?? throw new FileNotFoundException( "skillgrp.mul not found." );
        ( IsUnicode, Groups, SkillGroupIndices ) = Parse( File.ReadAllBytes( path ) );
    }

    /// <summary>True for the newer format with UTF-16 names.</summary>
    public bool IsUnicode { get; }

    public IReadOnlyList<SkillGroup> Groups { get; }

    /// <summary>Group index for each skill index.</summary>
    public IReadOnlyList<int> SkillGroupIndices { get; }

    /// <summary>The group of skill <paramref name="skill" />, or null if the file does not list it.</summary>
    public SkillGroup? GetGroupOf( int skill ) =>
        ( uint )skill < ( uint )SkillGroupIndices.Count && ( uint )SkillGroupIndices[skill] < ( uint )Groups.Count ? Groups[SkillGroupIndices[skill]] : null;

    public static (bool Unicode, IReadOnlyList<SkillGroup> Groups, IReadOnlyList<int> SkillGroups) Parse( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 4 )
        {
            throw new InvalidDataException( "skillgrp.mul is too short." );
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian( data );
        var unicode = count == -1;
        var start = 4;

        if ( unicode )
        {
            if ( data.Length < 8 )
            {
                throw new InvalidDataException( "skillgrp.mul is too short." );
            }

            count = BinaryPrimitives.ReadInt32LittleEndian( data[4..] );
            start = 8;
        }

        var nameLength = unicode ? 34 : 17;

        if ( count < 1 || start + ( long )( count - 1 ) * nameLength > data.Length )
        {
            throw new InvalidDataException( $"skillgrp.mul has an invalid group count ({count})." );
        }

        var names = new string[count];
        names[0] = MiscName;

        for ( var i = 1; i < count; i++ )
        {
            var raw = data.Slice( start + ( i - 1 ) * nameLength, nameLength );
            names[i] = unicode ? ReadUnicode( raw ) : ReadAscii( raw );
        }

        var tail = data[( start + ( count - 1 ) * nameLength )..];
        var skills = new int[tail.Length / 4];

        for ( var i = 0; i < skills.Length; i++ )
        {
            skills[i] = BinaryPrimitives.ReadInt32LittleEndian( tail[( i * 4 )..] );
        }

        var members = new List<int>[count];

        for ( var i = 0; i < count; i++ )
        {
            members[i] = [];
        }

        for ( var skill = 0; skill < skills.Length; skill++ )
        {
            if ( ( uint )skills[skill] < ( uint )count )
            {
                members[skills[skill]].Add( skill );
            }
        }

        var groups = new SkillGroup[count];

        for ( var i = 0; i < count; i++ )
        {
            groups[i] = new SkillGroup( i, names[i], members[i] );
        }

        return ( unicode, groups, skills );
    }

    private static string ReadAscii( ReadOnlySpan<byte> raw )
    {
        var end = raw.IndexOf( ( byte )0 );
        return Encoding.ASCII.GetString( end < 0 ? raw : raw[..end] );
    }

    private static string ReadUnicode( ReadOnlySpan<byte> raw )
    {
        var length = 0;

        while ( length + 1 < raw.Length && ( raw[length] | raw[length + 1] ) != 0 )
        {
            length += 2;
        }

        return Encoding.Unicode.GetString( raw[..length] );
    }
}
