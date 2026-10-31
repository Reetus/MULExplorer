using System.Globalization;
using UODataAccess.Imaging;

namespace UODataAccess.Loaders;

/// <summary>The playable bodies the Dress preview can wear items on.</summary>
public enum DressBody
{
    HumanMale,
    HumanFemale,
    ElfMale,
    ElfFemale,
    GargoyleMale,
    GargoyleFemale
}

/// <summary>An equipped item (tiledata id) with an optional game hue.</summary>
public readonly record struct DressItem( int ItemId, int Hue = 0 );

/// <summary>
///     Read-only outfit preview, as in UOFiddler's Dress tab: paperdoll gumps (body gump plus each
///     item's <c>animation + 50000</c> male / <c>+ 60000</c> female gump) and the in-world figure
///     (body animation plus each item's animation, composited in layer order). Item animations are
///     converted per body through Equipconv.def; missing paperdoll gumps fall back to the other
///     gender and then gump.def.
/// </summary>
public sealed class DressRenderer( UOData data )
{
    public const int MaleGumpOffset = 50000;
    public const int FemaleGumpOffset = 60000;

    /// <summary>Paperdoll layer order (UOFiddler <c>_drawOrder</c>); layer 0 is the body.</summary>
    public static IReadOnlyList<int> PaperdollOrder { get; } =
    [
        0x00, 0x14, 0x05, 0x04, 0x03, 0x18, 0x13, 0x0D, 0x11, 0x08, 0x09, 0x0E, 0x07, 0x17, 0x0A, 0x0B, 0x0C, 0x16, 0x10, 0x12, 0x06, 0x01, 0x02, 0x15
    ];

    /// <summary>Figure layer order when the cloak is behind the body (UOFiddler <c>_drawOrder2</c>).</summary>
    private static readonly int[] FigureOrderCloakLast =
    [
        0x00, 0x05, 0x04, 0x03, 0x18, 0x13, 0x0D, 0x11, 0x08, 0x09, 0x0E, 0x07, 0x17, 0x0A, 0x0B, 0x0C, 0x16, 0x10, 0x12, 0x06, 0x01, 0x02, 0x14, 0x15
    ];

    private Dictionary<int, (int Gump, int Hue)>? _gumpDef;

    public UOData Data { get; } = data;

    /// <summary>Layers an item can be worn on (everything in the paperdoll order except the body).</summary>
    public static bool IsDressLayer( int layer ) => layer != 0 && PaperdollOrder.Contains( layer );

    public static bool IsFemale( DressBody body ) => body is DressBody.HumanFemale or DressBody.ElfFemale or DressBody.GargoyleFemale;

    public static int AnimationBody( DressBody body ) => body switch
    {
        DressBody.HumanFemale => 401,
        DressBody.ElfMale => 605,
        DressBody.ElfFemale => 606,
        DressBody.GargoyleMale => 666,
        DressBody.GargoyleFemale => 667,
        _ => 400
    };

    public static int BodyGump( DressBody body ) => body switch
    {
        DressBody.HumanFemale => 0x0D,
        DressBody.ElfMale => 0x0E,
        DressBody.ElfFemale => 0x0F,
        DressBody.GargoyleMale => 0x029A,
        DressBody.GargoyleFemale => 0x0299,
        _ => 0x0C
    };

    /// <summary>Whether a tiledata item can be dressed: wearable, on a known layer, with an animation id.</summary>
    public bool IsWearable( int itemId )
    {
        var item = Data.TileData.GetItem( itemId );
        return item.Flags.HasFlag( TileFlag.Wearable ) && IsDressLayer( item.Layer ) && item.Animation != 0;
    }

    /// <summary>Animation body used for an item's animation id on <paramref name="body" /> (Equipconv.def applied).</summary>
    public int ItemAnimation( DressBody body, int animation ) =>
        Data.Animations.TryGetEquipConversion( AnimationBody( body ), animation, out var conversion ) ? conversion.Animation : animation;

    /// <summary>Paperdoll gump of an item on <paramref name="body" />, or null; <paramref name="hue" /> receives a gump.def hue if one applies.</summary>
    public int? PaperdollGump( DressBody body, int itemId, out int hue )
    {
        hue = 0;
        var gumps = Data.Gumps;
        var animation = ( int )Data.TileData.GetItem( itemId ).Animation;

        if ( Data.Animations.TryGetEquipConversion( AnimationBody( body ), animation, out var conversion ) )
        {
            animation = conversion.Gump >= FemaleGumpOffset ? conversion.Gump - FemaleGumpOffset
                : conversion.Gump > MaleGumpOffset ? conversion.Gump - MaleGumpOffset
                : conversion.Gump;
        }

        var preferred = animation + ( IsFemale( body ) ? FemaleGumpOffset : MaleGumpOffset );
        var other = animation + ( IsFemale( body ) ? MaleGumpOffset : FemaleGumpOffset );

        if ( gumps.IsValid( preferred ) )
        {
            return preferred;
        }

        if ( gumps.IsValid( other ) )
        {
            return other;
        }

        foreach ( var candidate in new[] { preferred, other } )
        {
            if ( GumpDef().TryGetValue( candidate, out var def ) && gumps.IsValid( def.Gump ) )
            {
                hue = def.Hue;
                return def.Gump;
            }
        }

        return null;
    }

    /// <summary>Body gump plus each item's gump, back to front.</summary>
    public Pixmap? RenderPaperdoll( DressBody body, int bodyHue, IReadOnlyList<DressItem> items )
    {
        var layers = new List<Pixmap>();

        if ( Data.Gumps.Get( BodyGump( body ) ) is { } bodyGump )
        {
            layers.Add( bodyHue > 0 ? Data.Hues.Apply( bodyGump, bodyHue, true ) : bodyGump );
        }

        foreach ( var item in Sorted( items, PaperdollOrder ) )
        {
            if ( PaperdollGump( body, item.ItemId, out var defHue ) is not { } gump || Data.Gumps.Get( gump ) is not { } pixmap )
            {
                continue;
            }

            var hue = item.Hue > 0 ? item.Hue : defHue;
            layers.Add( hue > 0 ? Data.Hues.Apply( pixmap, hue, Data.TileData.GetItem( item.ItemId ).Flags.HasFlag( TileFlag.PartialHue ) ) : pixmap );
        }

        if ( layers.Count == 0 )
        {
            return null;
        }

        var canvas = new Pixmap( layers.Max( l => l.Width ), layers.Max( l => l.Height ) );

        foreach ( var layer in layers )
        {
            canvas.Blit( layer, 0, 0 );
        }

        return canvas;
    }

    /// <summary>
    ///     The animated figure: every frame of <paramref name="action" /> in <paramref name="direction" />
    ///     (0..7, see <see cref="Animations.GetFrames" />) with all items composited, all frames on one
    ///     canvas size so they line up.
    /// </summary>
    public Pixmap[] RenderFigure( DressBody body, int bodyHue, IReadOnlyList<DressItem> items, int action, int direction )
    {
        var animations = Data.Animations;
        var layers = new List<AnimationFrame[]>();
        var bodyFrames = animations.GetFrames( AnimationBody( body ), action, direction );

        if ( bodyFrames.Length > 0 )
        {
            layers.Add( bodyHue > 0 ? Animations.ApplyHue( bodyFrames, Data.Hues, bodyHue ) : bodyFrames );
        }

        // UOFiddler draws the cloak in front only when facing away from the viewer.
        var order = ( ( direction - 3 ) & 7 ) is >= 4 and <= 6 ? PaperdollOrder : FigureOrderCloakLast;

        foreach ( var item in Sorted( items, order ) )
        {
            var animation = ItemAnimation( body, Data.TileData.GetItem( item.ItemId ).Animation );
            var info = animations.GetBody( animation );

            if ( info.Type is not (MobType.Equipment or MobType.Human) || !info.IsDefined( action ) )
            {
                continue;
            }

            var frames = animations.GetFrames( animation, action, direction );

            if ( frames.Length == 0 )
            {
                continue;
            }

            layers.Add( item.Hue > 0
                ? Animations.ApplyHue( frames, Data.Hues, item.Hue, Data.TileData.GetItem( item.ItemId ).Flags.HasFlag( TileFlag.PartialHue ) )
                : frames );
        }

        return Animations.RenderLayers( layers );
    }

    /// <summary>Items in drawing order for <paramref name="order" />; items on layers not in the order are skipped.</summary>
    private IEnumerable<DressItem> Sorted( IReadOnlyList<DressItem> items, IReadOnlyList<int> order )
    {
        foreach ( var layer in order )
        {
            if ( layer == 0 )
            {
                continue;
            }

            foreach ( var item in items )
            {
                if ( Data.TileData.GetItem( item.ItemId ).Layer == layer )
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>gump.def: <c>gump {replacement} hue</c>, used when a paperdoll gump is missing.</summary>
    private Dictionary<int, (int Gump, int Hue)> GumpDef()
    {
        if ( _gumpDef is not null )
        {
            return _gumpDef;
        }

        var map = new Dictionary<int, (int Gump, int Hue)>();

        if ( Data.Files.Find( "gump.def" ) is { } path )
        {
            foreach ( var raw in File.ReadLines( path ) )
            {
                var line = raw.Trim();
                var open = line.IndexOf( '{' );
                var close = line.IndexOf( '}' );

                if ( line.StartsWith( '#' ) || open <= 0 || close < open ||
                     !int.TryParse( line.AsSpan( 0, open ).Trim(), CultureInfo.InvariantCulture, out var id ) ||
                     !int.TryParse( line.AsSpan( open + 1, close - open - 1 ).Trim(), CultureInfo.InvariantCulture, out var target ) )
                {
                    continue;
                }

                var rest = line.AsSpan( close + 1 ).Trim();
                var comment = rest.IndexOf( '#' );
                _ = int.TryParse( comment >= 0 ? rest[..comment].Trim() : rest, CultureInfo.InvariantCulture, out var hue );
                map.TryAdd( id, ( target, hue ) );
            }
        }

        return _gumpDef = map;
    }
}
