using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Loader tests against a real client (skipped when none is available).</summary>
public sealed class DataTests
{
    [Fact]
    public void TileData_LoadsKnownItem()
    {
        var data = UOTestData.Require();

        Assert.Equal( 0x4000, data.TileData.LandTable.Length );
        Assert.True( data.TileData.ItemTable.Length >= 0x4000 );
        Assert.Equal( "gold coin", data.TileData.GetItem( 0x0EED ).Name );
        Assert.True( data.TileData.GetItem( 0x0EED ).Flags.HasFlag( TileFlag.Generic ) );
    }

    [Fact]
    public void Art_DecodesItemAndLand()
    {
        var data = UOTestData.Require();

        var coin = data.Art.GetItem( 0x0EED );
        Assert.NotNull( coin );
        Assert.Equal( data.Art.GetItemSize( 0x0EED ), ( coin.Width, coin.Height ) );
        Assert.NotNull( coin.OpaqueBounds() );

        var grass = data.Art.GetLand( 3 );
        Assert.NotNull( grass );
        Assert.Equal( 44, grass.Width );
        Assert.Equal( 0u, grass[0, 0] );            // diamond corners are transparent
        Assert.NotEqual( 0u, grass[22, 22] );       // centre is opaque
    }

    [Fact]
    public void Gumps_DecodeWithSizeMatchingIndex()
    {
        var data = UOTestData.Require();
        var id = Enumerable.Range( 0, 0x1000 ).First( data.Gumps.IsValid );

        var gump = data.Gumps.Get( id );

        Assert.NotNull( gump );
        Assert.Equal( data.Gumps.GetSize( id ), ( gump.Width, gump.Height ) );
        Assert.NotNull( gump.OpaqueBounds() );
    }

    [Fact]
    public void Hues_LoadAndApply()
    {
        var data = UOTestData.Require();

        Assert.True( data.Hues.List.Count >= 3000 );
        Assert.Null( data.Hues.Get( 0 ) );
        Assert.Same( data.Hues.List[0], data.Hues.Get( 1 ) );

        var coin = data.Art.GetItem( 0x0EED )!;
        var hued = data.Hues.Apply( coin, 0x21 );
        Assert.Equal( coin.Width, hued.Width );
        Assert.NotEqual( coin.Pixels, hued.Pixels );
    }

    [Fact]
    public void Cliloc_LoadsEnglish()
    {
        var data = UOTestData.Require();

        Assert.Contains( "enu", Cliloc.AvailableLanguages( data.Files ) );
        Assert.True( data.Cliloc.Count > 10_000 );
        var first = data.Cliloc.Entries[0];
        Assert.Equal( first.Text, data.Cliloc.GetString( first.Number ) );
    }

    [Fact]
    public void Cliloc_FormatsArguments()
    {
        var data = UOTestData.Require();
        var entry = data.Cliloc.Entries.First( e => e.Text.Contains( "~1_" ) );

        var formatted = data.Cliloc.Format( entry.Number, "XYZZY" );

        Assert.Contains( "XYZZY", formatted );
        Assert.DoesNotContain( "~1_", formatted );
    }

    [Fact]
    public void RadarColors_CoverLandAndItems()
    {
        var data = UOTestData.Require();

        Assert.True( data.RadarColors.ItemCount >= 0x4000 );
        Assert.NotEqual( ( ushort )0, data.RadarColors.GetLandColor( 3 ) ); // grass
        Assert.Equal( ( ushort )0, data.RadarColors.GetItemColor( -1 ) );
    }

    [Fact]
    public void UOData_TryGetReturnsNullForMissingAssets()
    {
        using var dir = new TempDir();
        using var data = new UODataAccess.UOData( dir.Path );

        Assert.Null( data.TryGet( d => d.TileData ) );
        Assert.Null( data.TryGet( d => d.Art ) );
    }
}
