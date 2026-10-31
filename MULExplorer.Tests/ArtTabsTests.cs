using System.Buffers.Binary;
using UODataAccess.Imaging;
using UODataAccess.IO;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Loaders behind the Multis, Land Tiles, Textures, TileData and RadarColor tabs.</summary>
public sealed class ArtTabsTests
{
    // ----- synthetic formats -----

    private static byte[] MULRecords( int recordSize, params (ushort Id, short X, short Y, short Z, int Flags)[] tiles )
    {
        var data = new byte[tiles.Length * recordSize];

        for ( var i = 0; i < tiles.Length; i++ )
        {
            var r = data.AsSpan( i * recordSize );
            BinaryPrimitives.WriteUInt16LittleEndian( r, tiles[i].Id );
            BinaryPrimitives.WriteInt16LittleEndian( r[2..], tiles[i].X );
            BinaryPrimitives.WriteInt16LittleEndian( r[4..], tiles[i].Y );
            BinaryPrimitives.WriteInt16LittleEndian( r[6..], tiles[i].Z );
            BinaryPrimitives.WriteInt32LittleEndian( r[8..], tiles[i].Flags );

            if ( recordSize == 16 )
            {
                BinaryPrimitives.WriteInt32LittleEndian( r[12..], 7 );
            }
        }

        return data;
    }

    private static void WriteMultiMUL( TempDir dir, int recordSize, params byte[][] entries )
    {
        using var idx = new BinaryWriter( File.Create( dir.File( "multi.idx" ) ) );
        using var mul = File.Create( dir.File( "multi.mul" ) );

        foreach ( var entry in entries )
        {
            idx.Write( entry.Length == 0 ? -1 : ( int )mul.Position );
            idx.Write( entry.Length == 0 ? -1 : entry.Length );
            idx.Write( 0 );
            mul.Write( entry );
        }
    }

    [Theory]
    [InlineData( 12 )]
    [InlineData( 16 )]
    public void Multis_MUL_DetectsRecordSize( int recordSize )
    {
        using var dir = new TempDir();
        // 5 records: 60 vs 80 bytes, only one record size divides each.
        var a = MULRecords( recordSize, ( 0x10, 0, 0, 0, 1 ), ( 0x11, -1, 2, 5, 1 ), ( 0x12, 3, -2, 20, 0 ), ( 0x13, 0, 0, 1, 1 ), ( 0x13, 0, 0, 2, 1 ) );
        WriteMultiMUL( dir, recordSize, a, [], a );

        using var multis = new Multis( new UOFileSet( dir.Path ) );

        Assert.Equal( recordSize, multis.MULRecordSize );
        Assert.False( multis.HasUOP );
        Assert.Equal( MultiSource.MUL, multis.DefaultSource );
        Assert.Equal( [0, 2], multis.GetIds( MultiSource.MUL ) );
        Assert.Null( multis.Get( 1 ) );

        var m = multis.Get( 0 )!;
        Assert.Equal( 5, m.Tiles.Length );
        Assert.Equal( new MultiTile( 0x11, -1, 2, 5, 1, recordSize == 16 ? 7 : 0, 1, true ), m.Tiles[1] );
        Assert.False( m.Tiles[2].IsVisible );
        Assert.Equal( ( -1, -2, 3, 2 ), ( m.MinX, m.MinY, m.MaxX, m.MaxY ) );
        Assert.Equal( ( 5, 5 ), ( m.Width, m.Height ) );
        Assert.Equal( 20, m.MaxHeight );
        Assert.Equal( 3, m.Surface );
    }

    [Fact]
    public void Multis_RecordSize_AmbiguousFallsBackToHint()
    {
        Assert.Equal( 16, Multis.DetectRecordSize( [48, 96], true ) );
        Assert.Equal( 12, Multis.DetectRecordSize( [48, 96], false ) );
        Assert.Equal( 12, Multis.DetectRecordSize( [48, 36], true ) );
        Assert.Equal( 16, Multis.DetectRecordSize( [48, 32], false ) );
    }

    [Fact]
    public void Multis_UOP_ParsesComponentsAndSkipsClilocs()
    {
        using var dir = new TempDir();
        var payload = new byte[8 + 14 + 4 * 2 + 14];
        var s = payload.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian( s, 5 );
        BinaryPrimitives.WriteInt32LittleEndian( s[4..], 2 );
        // tile 1: flags 0 (visible), two clilocs
        BinaryPrimitives.WriteUInt16LittleEndian( s[8..], 0x100 );
        BinaryPrimitives.WriteInt16LittleEndian( s[10..], -2 );
        BinaryPrimitives.WriteInt16LittleEndian( s[12..], 3 );
        BinaryPrimitives.WriteInt16LittleEndian( s[14..], 7 );
        BinaryPrimitives.WriteUInt16LittleEndian( s[16..], 0 );
        BinaryPrimitives.WriteInt32LittleEndian( s[18..], 2 );
        // tile 2: flags 1 (invisible), 0x101 would also set Unk1
        var t2 = s[( 8 + 14 + 8 )..];
        BinaryPrimitives.WriteUInt16LittleEndian( t2, 0x200 );
        BinaryPrimitives.WriteUInt16LittleEndian( t2[8..], 0x101 );

        UOPWriter.Write( dir.File( "MultiCollection.uop" ),
        [
            ( "build/multicollection/000005.bin", payload, 0, payload.Length, UOPCompression.None ),
            ( "build/multicollection/housing.bin", [7, 0, 0, 0, 1, 0, 0, 0], 0, 8, UOPCompression.None )
        ] );

        using var multis = new Multis( new UOFileSet( dir.Path ) );

        Assert.True( multis.HasUOP );
        Assert.True( multis.HasHousingBin );
        Assert.Equal( [5], multis.GetIds( MultiSource.UOP ) );

        var m = multis.Get( 5 )!;
        Assert.Equal( MultiSource.UOP, m.Source );
        Assert.Equal( new MultiTile( 0x100, -2, 3, 7, 1, 0, 0, true ), m.Tiles[0] );
        Assert.Equal( new MultiTile( 0x200, 0, 0, 0, 0, 1, 0x101, false ), m.Tiles[1] );
    }

    [Fact]
    public void Textures_SizeFollowsLengthAndDefIsParsed()
    {
        var small = Textures.Decode( new byte[64 * 64 * 2] );
        var large = Textures.Decode( new byte[128 * 128 * 2] );

        Assert.Equal( 64, small!.Width );
        Assert.Equal( 128, large!.Height );
        Assert.Null( Textures.Decode( new byte[100] ) );
        Assert.Equal( 0xFF000000u, small[3, 3] ); // opaque black, textures have no transparency

        var aliases = new Dictionary<int, int>();
        Textures.ParseDef( ["# comment", "2500  {3} 1645", "12 {4, 5}", "junk"], aliases );
        Assert.Equal( 3, aliases[2500] );
        Assert.Equal( 5, aliases[12] );
        Assert.Equal( 2, aliases.Count );
    }

    // ----- real client -----

    [Fact]
    public void Multis_RealData_UOPAndMULAgree()
    {
        var data = UOTestData.Require();
        var multis = data.Multis;
        Assert.SkipUnless( multis.HasUOP && multis.HasMUL, "Needs both MultiCollection.uop and multi.mul." );

        var uopIds = multis.GetIds( MultiSource.UOP );
        Assert.True( uopIds.Count > 500 );
        Assert.Equal( 16, multis.MULRecordSize );

        // Small boat north: same component count and positions in both sources.
        var uop = multis.Get( 0, MultiSource.UOP )!;
        var mul = multis.Get( 0, MultiSource.MUL )!;
        Assert.Equal( mul.Tiles.Length, uop.Tiles.Length );
        Assert.Equal( mul.Tiles.Select( t => ( t.ItemId, t.X, t.Y, t.Z ) ).Order(), uop.Tiles.Select( t => ( t.ItemId, t.X, t.Y, t.Z ) ).Order() );
        Assert.Equal( "Small Boat [north]", MultiNames.GetName( 0 ) );
    }

    [Fact]
    public void MultiRenderer_DrawsHouseAndHeightCutKeepsCanvas()
    {
        var data = UOTestData.Require();
        var id = MultiNames.All.First( kv => kv.Value.Type == MultiKind.House && data.Multis.IsValid( kv.Key ) ).Key;
        var multi = data.Multis.Get( id )!;

        var full = MultiRenderer.Render( multi, data.Art, data.TileData )!;
        var cut = MultiRenderer.Render( multi, data.Art, data.TileData, maxHeight: 0 )!;

        Assert.True( full.Width > 44 && full.Height > 44 );
        Assert.Equal( ( full.Width, full.Height ), ( cut.Width, cut.Height ) );
        Assert.True( Opaque( full ) > Opaque( cut ) );
        Assert.True( Opaque( cut ) > 0 );

        static int Opaque( Pixmap p ) => p.Pixels.Count( px => px >> 24 != 0 );
    }

    [Fact]
    public void Textures_RealData()
    {
        var data = UOTestData.Require();
        var textures = data.Textures;
        var ids = Enumerable.Range( 0, textures.Count ).Where( textures.IsValid ).ToList();

        Assert.True( ids.Count > 1000 );
        Assert.Contains( ids, i => textures.GetSize( i ) == 128 );
        Assert.Contains( ids, i => textures.GetSize( i ) == 64 );

        var tex = textures.Get( ids[0] )!;
        Assert.Equal( textures.GetSize( ids[0] ), tex.Width );
        Assert.All( tex.Pixels, p => Assert.Equal( 0xFFu, p >> 24 ) );

        // Grass land tile 3 points at a texture that exists.
        var landTexture = data.TileData.GetLand( 3 ).TextureId;
        Assert.True( textures.IsValid( landTexture ) );
    }

    [Fact]
    public void RadarColors_CoverLandAndItems()
    {
        var data = UOTestData.Require();

        Assert.True( data.RadarColors.ItemCount >= 0x4000 );
        Assert.NotEqual( 0, data.RadarColors.GetLandColor( 3 ) );
        Assert.Equal( 0xFFu, data.RadarColors.GetLandArgb( 3 ) >> 24 );
    }
}
