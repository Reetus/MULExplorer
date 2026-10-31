using System.Buffers.Binary;
using System.Text;
using UODataAccess;
using UODataAccess.IO;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Builders for the small synthetic client files used by the Sounds/Speech/Skills/SkillGrp/Verdata tests.</summary>
internal static class TextAudioFiles
{
    /// <summary>A sound entry as stored: 40-byte name block then PCM.</summary>
    public static byte[] SoundEntry( string name, ReadOnlySpan<byte> pcm )
    {
        var entry = new byte[Sounds.HeaderLength + pcm.Length];
        Encoding.ASCII.GetBytes( name ).CopyTo( entry, 0 );
        entry.AsSpan( 30, 10 ).Fill( 0xCD ); // uninitialised garbage after the name, as in real files
        pcm.CopyTo( entry.AsSpan( Sounds.HeaderLength ) );
        return entry;
    }

    /// <summary>Writes an <c>xxxidx.mul</c> + <c>xxx.mul</c> pair; null entries become empty slots.</summary>
    public static void WriteIndexed( string idxPath, string mulPath, IReadOnlyList<byte[]?> entries, Func<int, int>? extra = null )
    {
        using var idx = new BinaryWriter( File.Create( idxPath ) );
        using var mul = new BinaryWriter( File.Create( mulPath ) );

        for ( var i = 0; i < entries.Count; i++ )
        {
            if ( entries[i] is { } e )
            {
                idx.Write( ( int )mul.BaseStream.Position );
                idx.Write( e.Length );
                idx.Write( extra?.Invoke( i ) ?? 0 );
                mul.Write( e );
            }
            else
            {
                idx.Write( -1 );
                idx.Write( 0 );
                idx.Write( 0 );
            }
        }
    }

    public static byte[] SpeechRecord( int id, string keyword )
    {
        var text = Encoding.UTF8.GetBytes( keyword );
        var record = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian( record, ( ushort )id );
        BinaryPrimitives.WriteUInt16BigEndian( record.AsSpan( 2 ), ( ushort )text.Length );
        text.CopyTo( record, 4 );
        return record;
    }

    public static byte[] SkillEntry( bool action, string name ) => [( byte )( action ? 1 : 0 ), .. Encoding.ASCII.GetBytes( name ), 0];

    public static byte[] SkillGroupFile( bool unicode, string[] names, int[] skillGroups )
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter( ms );

        if ( unicode )
        {
            w.Write( -1 );
        }

        w.Write( names.Length + 1 );

        foreach ( var name in names )
        {
            var buffer = new byte[unicode ? 34 : 17];
            ( unicode ? Encoding.Unicode : Encoding.ASCII ).GetBytes( name ).CopyTo( buffer, 0 );
            w.Write( buffer );
        }

        foreach ( var g in skillGroups )
        {
            w.Write( g );
        }

        return ms.ToArray();
    }

    /// <summary>verdata.mul with the given records followed by their payloads (lookups are filled in).</summary>
    public static byte[] VerdataFile( IReadOnlyList<(int File, int Index, int Extra, byte[] Data)> patches, int? declaredCount = null )
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter( ms );
        w.Write( declaredCount ?? patches.Count );
        var lookup = 4 + patches.Count * 20;

        foreach ( var p in patches )
        {
            w.Write( p.File );
            w.Write( p.Index );
            w.Write( lookup );
            w.Write( p.Data.Length );
            w.Write( p.Extra );
            lookup += p.Data.Length;
        }

        foreach ( var p in patches )
        {
            w.Write( p.Data );
        }

        return ms.ToArray();
    }

    public static byte[] Pcm( int samples )
    {
        var pcm = new byte[samples * 2];

        for ( var i = 0; i < samples; i++ )
        {
            BinaryPrimitives.WriteInt16LittleEndian( pcm.AsSpan( i * 2 ), ( short )( i * 37 ) );
        }

        return pcm;
    }
}

/// <summary>Loader tests for sounds, speech, skills, skill groups and verdata: synthetic formats plus the real client.</summary>
public sealed class TextAudioDataTests
{
    // ----- Sounds -----

    [Fact]
    public void Sounds_MUL_ReadsNameDurationAndAppliesSoundDef()
    {
        using var dir = new TempDir();
        var pcm = TextAudioFiles.Pcm( 22050 ); // one second
        TextAudioFiles.WriteIndexed( dir.File( "soundidx.mul" ), dir.File( "sound.mul" ),
            [TextAudioFiles.SoundEntry( "door1.wav", pcm ), null, TextAudioFiles.SoundEntry( "bell.wav", pcm.AsSpan( 0, 22050 ) ), null, null] );
        File.WriteAllText( dir.File( "Sound.def" ), "# comment\n1 {2} 0\n3 {-1} 0\n4 {7, 0} 0\n0 {2} 0\n" );

        using var sounds = new Sounds( new UOFileSet( dir.Path ) );

        Assert.Equal( "MUL", sounds.Source );
        var door = sounds.GetInfo( 0 )!.Value;
        Assert.Equal( "door1.wav", door.Name );          // stops at the NUL, ignores the garbage after it
        Assert.Equal( 1.0, door.Duration, 6 );
        Assert.False( door.IsRemapped );                 // valid slots ignore Sound.def

        var remapped = sounds.GetInfo( 1 )!.Value;
        Assert.True( remapped.IsRemapped );
        Assert.Equal( 2, remapped.SourceId );
        Assert.Equal( "bell.wav", remapped.Name );

        Assert.Null( sounds.GetInfo( 3 ) );              // {-1}: stays empty
        Assert.Equal( 0, sounds.GetInfo( 4 )!.Value.SourceId ); // 7 is empty, 0 is valid: last valid wins
        Assert.Equal( [0, 1, 2, 4], sounds.GetAll().Select( s => s.Id ) );
        Assert.Equal( pcm, sounds.GetPCM( 0 ) );
    }

    [Fact]
    public void Sounds_UOP_IsPreferredAndDecoded()
    {
        using var dir = new TempDir();
        var entry = TextAudioFiles.SoundEntry( "uop.wav", TextAudioFiles.Pcm( 100 ) );
        UOPWriter.Write( dir.File( "soundLegacyMUL.uop" ), [( "build/soundlegacymul/00000005.dat", entry, 0, entry.Length, UOPCompression.None )] );

        using var sounds = new Sounds( new UOFileSet( dir.Path ) );

        Assert.Equal( "UOP", sounds.Source );
        Assert.Equal( "uop.wav", sounds.GetInfo( 5 )!.Value.Name );
        Assert.Equal( 200, sounds.GetInfo( 5 )!.Value.SampleBytes );
        Assert.Null( sounds.GetInfo( 4 ) );
    }

    [Fact]
    public void Sounds_MissingFilesThrowFileNotFound()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>( () => new Sounds( new UOFileSet( dir.Path ) ) );
    }

    [Fact]
    public void Wav_HeaderIsCanonicalPCM()
    {
        var pcm = TextAudioFiles.Pcm( 1000 );
        var wav = Sounds.CreateWav( pcm );
        var s = wav.AsSpan();

        Assert.Equal( 44 + pcm.Length, wav.Length );
        Assert.True( s[..4].SequenceEqual( "RIFF"u8 ) );
        Assert.Equal( wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian( s[4..] ) );
        Assert.True( s[8..12].SequenceEqual( "WAVE"u8 ) );
        Assert.True( s[12..16].SequenceEqual( "fmt "u8 ) );
        Assert.Equal( 16, BinaryPrimitives.ReadInt32LittleEndian( s[16..] ) );
        Assert.Equal( 1, BinaryPrimitives.ReadInt16LittleEndian( s[20..] ) );      // PCM
        Assert.Equal( 1, BinaryPrimitives.ReadInt16LittleEndian( s[22..] ) );      // mono
        Assert.Equal( 22050, BinaryPrimitives.ReadInt32LittleEndian( s[24..] ) );  // sample rate
        Assert.Equal( 44100, BinaryPrimitives.ReadInt32LittleEndian( s[28..] ) );  // byte rate
        Assert.Equal( 2, BinaryPrimitives.ReadInt16LittleEndian( s[32..] ) );      // block align
        Assert.Equal( 16, BinaryPrimitives.ReadInt16LittleEndian( s[34..] ) );     // bits
        Assert.True( s[36..40].SequenceEqual( "data"u8 ) );
        Assert.Equal( pcm.Length, BinaryPrimitives.ReadInt32LittleEndian( s[40..] ) );
        Assert.True( s[44..].SequenceEqual( pcm ) );
    }

    [Fact]
    public void Sounds_RealClient()
    {
        var sounds = UOTestData.Require().Sounds;
        var all = sounds.GetAll();

        Assert.True( all.Count > 1000, $"{all.Count} sounds" );
        Assert.All( all.Take( 50 ), s => Assert.EndsWith( ".wav", s.Name, StringComparison.OrdinalIgnoreCase ) );
        Assert.All( sounds.Remaps, r => Assert.True( sounds.IsValid( r.Value ) ) );

        var first = all[0];
        var wav = sounds.GetWav( first.Id )!;
        Assert.Equal( 44 + first.SampleBytes, wav.Length );
        Assert.Equal( wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian( wav.AsSpan( 4 ) ) );
        Assert.InRange( first.Duration, 0.01, 60 );
    }

    // ----- Speech -----

    [Fact]
    public void Speech_ParsesBigEndianUTF8AndSkipsEmpty()
    {
        byte[] data = [.. TextAudioFiles.SpeechRecord( 2, "*bank*" ), .. TextAudioFiles.SpeechRecord( 3, "" ), .. TextAudioFiles.SpeechRecord( 0x0102, "guards*" ), .. TextAudioFiles.SpeechRecord( 7, "提領" )];

        var entries = Speech.Parse( data );

        Assert.Equal( 3, entries.Count );
        Assert.Equal( ( 0, 2, "*bank*" ), ( entries[0].Order, entries[0].Id, entries[0].Keyword ) );
        Assert.Equal( 0x0102, entries[1].Id );
        Assert.Equal( 1, entries[1].Order );
        Assert.Equal( "提領", entries[2].Keyword );
    }

    [Theory]
    [InlineData( "*bank*", "I wish to bank please", true )]
    [InlineData( "*bank*", "banker", false )]      // whole words only
    [InlineData( "*bank*", "bank!", true )]
    [InlineData( "guards*", "guards help", true )]  // must start with it
    [InlineData( "guards*", "call the guards", false )]
    [InlineData( "*vendor buy", "vendor buy", true )]
    [InlineData( "*vendor buy", "vendor buy now", false )] // must end with it
    public void Speech_KeywordMatchingFollowsClient( string keyword, string phrase, bool expected ) =>
        Assert.Equal( expected, new SpeechEntry( 0, 1, keyword ).Matches( phrase ) );

    [Fact]
    public void Speech_RealClient()
    {
        var speech = UOTestData.Require().Speech;

        Assert.True( speech.Count > 1000 );
        Assert.Contains( speech.Entries, e => e.Keyword == "*bank*" );
        Assert.Contains( speech.GetKeywords( "I wish to bank" ), e => e.Keyword == "*bank*" );
    }

    // ----- Skills -----

    [Fact]
    public void Skills_SyntheticSkipsEmptySlotsAndNumbersConsecutively()
    {
        using var dir = new TempDir();
        TextAudioFiles.WriteIndexed( dir.File( "Skills.idx" ), dir.File( "skills.mul" ),
            [TextAudioFiles.SkillEntry( false, "Alchemy" ), null, TextAudioFiles.SkillEntry( true, "Anatomy" )], i => i + 10 );

        var skills = new Skills( new UOFileSet( dir.Path ) );

        Assert.Equal( 2, skills.Count );
        Assert.Equal( new SkillInfo( 0, "Alchemy", false, 10 ), skills.Get( 0 ) );
        Assert.Equal( new SkillInfo( 1, "Anatomy", true, 12 ), skills.Get( 1 ) );
        Assert.Null( skills.Get( 2 ) );
    }

    [Fact]
    public void Skills_RealClient()
    {
        var skills = UOTestData.Require().Skills;

        Assert.True( skills.Count >= 49 );
        Assert.Equal( "Alchemy", skills.Entries[0].Name );
        Assert.True( skills.Entries[1].IsAction );     // Anatomy
        Assert.False( skills.Entries[0].IsAction );
    }

    // ----- Skill groups -----

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public void SkillGroups_ParseOldAndNewFormat( bool unicode )
    {
        var data = TextAudioFiles.SkillGroupFile( unicode, ["Combat", unicode ? "Ремесло" : "Trade Skills"], [1, 0, 2, 2, 9] );

        var (isUnicode, groups, skills) = SkillGroups.Parse( data );

        Assert.Equal( unicode, isUnicode );
        Assert.Equal( [SkillGroups.MiscName, "Combat", unicode ? "Ремесло" : "Trade Skills"], groups.Select( g => g.Name ) );
        Assert.Equal( [1, 0, 2, 2, 9], skills );
        Assert.Equal( [1], groups[0].Skills );
        Assert.Equal( [0], groups[1].Skills );
        Assert.Equal( [2, 3], groups[2].Skills ); // skill 4 points at a missing group and is left out
    }

    [Fact]
    public void SkillGroups_FromFileAndGroupLookup()
    {
        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "skillgrp.mul" ), TextAudioFiles.SkillGroupFile( false, ["Combat"], [1, 0] ) );

        var groups = new SkillGroups( new UOFileSet( dir.Path ) );

        Assert.False( groups.IsUnicode );
        Assert.Equal( "Combat", groups.GetGroupOf( 0 )!.Name );
        Assert.Equal( SkillGroups.MiscName, groups.GetGroupOf( 1 )!.Name );
        Assert.Null( groups.GetGroupOf( 2 ) );
    }

    [Fact]
    public void SkillGroups_RejectsBadCount() =>
        Assert.Throws<InvalidDataException>( () => SkillGroups.Parse( [0x40, 0, 0, 0, 1, 2, 3] ) );

    [Fact]
    public void SkillGroups_RealClient()
    {
        var data = UOTestData.Require();
        var groups = data.SkillGroups;

        Assert.Equal( SkillGroups.MiscName, groups.Groups[0].Name );
        Assert.True( groups.Groups.Count > 3 );
        Assert.Equal( data.Skills.Count, groups.SkillGroupIndices.Count );
        Assert.Equal( groups.SkillGroupIndices.Count, groups.Groups.Sum( g => g.Skills.Count ) );
    }

    // ----- Verdata -----

    [Fact]
    public void Verdata_MissingFileIsNotAnError()
    {
        using var dir = new TempDir();
        using var verdata = new Verdata( new UOFileSet( dir.Path ) );

        Assert.False( verdata.IsPresent );
        Assert.Empty( verdata.Patches );
        Assert.False( verdata.IsTruncated );
    }

    [Fact]
    public void Verdata_ReadsRecordsAndPayloads()
    {
        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "verdata.mul" ), TextAudioFiles.VerdataFile(
        [
            ( 4, 0x1234, 0, [1, 2, 3] ),
            ( 30, 7, unchecked( ( int )0x80000000 ), [9] ),
            ( 99, 1, 0, [] )
        ] ) );

        using var verdata = new Verdata( new UOFileSet( dir.Path ) );

        Assert.True( verdata.IsPresent );
        Assert.Equal( 3, verdata.Patches.Count );
        Assert.Equal( new VerdataPatch( 4, 0x1234, 64, 3, 0 ), verdata.Patches[0] );
        Assert.Equal( "art.mul", verdata.Patches[0].FileName );
        Assert.Equal( "tiledata.mul", verdata.Patches[1].FileName );
        Assert.Equal( "unknown", verdata.Patches[2].FileName );
        Assert.Equal( [1, 2, 3], verdata.GetData( verdata.Patches[0] ).ToArray() );
        Assert.Equal( [9], verdata.GetData( verdata.Patches[1] ).ToArray() );
        Assert.False( verdata.IsTruncated );
    }

    [Fact]
    public void Verdata_TruncatedTableReadsWhatIsThere()
    {
        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "verdata.mul" ), TextAudioFiles.VerdataFile( [( 12, 5, 0, [] )], declaredCount: 1000 ) );

        using var verdata = new Verdata( new UOFileSet( dir.Path ) );

        Assert.Single( verdata.Patches );
        Assert.True( verdata.IsTruncated );
        Assert.Equal( 1000, verdata.DeclaredCount );
    }

    [Fact]
    public void Verdata_RealClientDoesNotThrow()
    {
        var data = UOTestData.Require();
        var verdata = data.Verdata;

        Assert.Equal( data.Files.Exists( "verdata.mul" ), verdata.IsPresent );
    }
}
