using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>Animation group layout of a body (mobtypes.txt type column).</summary>
public enum MobType
{
    Monster = 0,
    SeaMonster = 1,
    Animal = 2,
    Human = 3,
    Equipment = 4
}

/// <summary>mobtypes.txt flag column (names follow ClassicUO's <c>AnimationFlags</c>).</summary>
[Flags]
public enum MobFlags : uint
{
    None = 0,
    Unknown1 = 0x1,
    Use2IfHitWhileRunning = 0x2,
    IdleAt8Frame = 0x4,
    CanFly = 0x8,
    Unknown10 = 0x10,
    CalculateOffsetLowGroupExtended = 0x20,
    CalculateOffsetByLowGroup = 0x40,
    Unknown80 = 0x80,
    Unknown100 = 0x100,
    Unknown200 = 0x200,
    CalculateOffsetByPeopleGroup = 0x400,
    Unknown800 = 0x800,
    Unknown1000 = 0x1000,
    Unknown2000 = 0x2000,
    Unknown4000 = 0x4000,
    Unknown8000 = 0x8000,
    UseUOPAnimation = 0x10000,
    Unknown20000 = 0x20000,
    Unknown40000 = 0x40000,
    Unknown80000 = 0x80000
}

public enum AnimationSource
{
    None,
    MUL,
    UOP
}

/// <summary>
///     One decoded frame. <see cref="CenterX" />/<see cref="CenterY" /> locate the feet: a frame is
///     drawn at <c>(anchorX - CenterX, anchorY - CenterY - Height)</c>. Missing frames have no image.
/// </summary>
public readonly record struct AnimationFrame( Pixmap? Image, int CenterX, int CenterY )
{
    public bool IsEmpty => Image is null;

    public int Width => Image?.Width ?? 0;

    public int Height => Image?.Height ?? 0;

    /// <summary>Top-left of the image relative to the anchor (feet) point.</summary>
    public int Left => -CenterX;

    public int Top => -CenterY - Height;
}

/// <summary>Equipconv.def entry: on <c>body</c>, an item with animation <c>id</c> uses these instead.</summary>
public readonly record struct EquipConversion( int Animation, int Gump, int Hue );

/// <summary>Where a body's frames come from after body.def/bodyconv.def/mobtypes.txt are applied.</summary>
public sealed record AnimationBody(
    int Body,
    MobType Type,
    bool HasMobType,
    MobFlags Flags,
    AnimationSource Source,
    string FileName,
    int FileBody,
    int? BodyDefTarget,
    int Hue,
    int ActionSlots,
    IReadOnlyList<int> Actions )
{
    public bool HasAnimation => Actions.Count > 0;

    public bool IsDefined( int action ) => Actions.Contains( action );
}

/// <summary>
///     Mobile and equipment animations. Legacy bodies live in <c>anim.mul</c>..<c>anim6.mul</c>
///     (5 stored directions per action, the other 3 mirrored; idx records grouped per body in
///     high/low/people blocks of 22/13/35 actions). Bodies flagged <c>UseUOPAnimation</c> in
///     mobtypes.txt come from <c>AnimationFrame*.uop</c>; <c>AnimationSequence.uop</c> supplies their
///     action aliases and, through its mode blocks, the role (stand/walk/die/...) of each group, since
///     these bodies do not follow the fixed action layout of their type. Body.def and Bodyconv.def
///     remap server bodies onto file bodies. Follows ClassicUO's <c>AnimationsLoader</c> for frames;
///     the mode blocks follow the client's own sequence reader. Everything is opened and decoded
///     lazily; decoded frame sets are cached.
/// </summary>
public sealed class Animations : IDisposable
{
    public const int MaxActions = 80;
    public const int MaxBodies = 2048;
    public const int Directions = 8;

    private const int StoredDirections = 5;
    private const int MaxCachedFrameSets = 512;

    private static readonly CompositeFormat UOPFramePattern = CompositeFormat.Parse( "build/animationlegacyframe/{0:D6}/{1:D2}.bin" );
    private static readonly CompositeFormat UOPSequencePattern = CompositeFormat.Parse( "build/animationsequence/{0:D8}.bin" );

    private readonly Lock _lock = new();
    private readonly Dictionary<int, (MobType Type, MobFlags Flags)> _mobTypes = new();
    private readonly Dictionary<int, (int Body, int Hue)> _bodyDef = new();
    private readonly Dictionary<int, (int Body, int Hue)> _corpseDef = new();
    private readonly Dictionary<int, (int FileIndex, int Body)> _bodyConv = new();
    private readonly Dictionary<int, Dictionary<int, EquipConversion>> _equipConv = new();
    private readonly Dictionary<int, AnimationSequence> _sequences = new();
    private readonly Dictionary<int, AnimationBody> _bodies = new();
    private readonly Dictionary<long, AnimationFrame[]> _frameCache = new();
    private readonly Queue<long> _frameCacheOrder = new();

    private readonly FileIndex?[] _mul = new FileIndex?[6];
    private readonly bool[] _mulOpened = new bool[6];
    private UOPArchive[]? _uop;

    public Animations( UOFileSet files )
    {
        Files = files;

        for ( var i = 0; i < 6; i++ )
        {
            if ( files.Exists( MULName( i ) ) && files.Exists( IdxName( i ) ) )
            {
                HasMUL = true;
            }
        }

        HasUOP = Enumerable.Range( 1, 10 ).Any( i => files.Exists( $"AnimationFrame{i}.uop" ) );

        if ( !HasMUL && !HasUOP )
        {
            throw new FileNotFoundException( "No animations found (anim.mul/anim.idx or AnimationFrame*.uop)." );
        }

        ReadMobTypes();
        ReadBodyDef( "Body.def", _bodyDef );
        ReadBodyDef( "Corpse.def", _corpseDef );
        ReadBodyConv();
        ReadEquipConv();
    }

    public UOFileSet Files { get; }

    public bool HasMUL { get; }

    public bool HasUOP { get; }

    /// <summary>Bodies listed in mobtypes.txt.</summary>
    public IReadOnlyCollection<int> MobTypeBodies => _mobTypes.Keys;

    /// <summary>Body.def remaps: body → (file body, hue).</summary>
    public IReadOnlyDictionary<int, (int Body, int Hue)> BodyDef => _bodyDef;

    /// <summary>Corpse.def remaps: body → (corpse body, hue).</summary>
    public IReadOnlyDictionary<int, (int Body, int Hue)> CorpseDef => _corpseDef;

    public void Dispose()
    {
        lock ( _lock )
        {
            foreach ( var index in _mul )
            {
                index?.Dispose();
            }

            Array.Clear( _mul );

            if ( _uop is not null )
            {
                foreach ( var archive in _uop )
                {
                    archive.Dispose();
                }

                _uop = null;
            }
        }
    }

    /// <summary>Bodies worth listing: UOFiddler's named list plus every mobtypes.txt body.</summary>
    public IEnumerable<int> CandidateBodies() =>
        AnimationNames.Entries.Select( e => e.Body ).Concat( _mobTypes.Keys ).Where( b => b is > 0 and < MaxBodies ).Distinct().Order();

    public bool TryGetMobType( int body, out MobType type, out MobFlags flags )
    {
        if ( _mobTypes.TryGetValue( body, out var entry ) )
        {
            ( type, flags ) = entry;
            return true;
        }

        type = MobType.Monster;
        flags = MobFlags.None;
        return false;
    }

    /// <summary>Equipconv.def lookup: how an item animation is replaced on <paramref name="body" /> (elves, gargoyles, gender swaps).</summary>
    public bool TryGetEquipConversion( int body, int animation, out EquipConversion conversion )
    {
        conversion = default;
        return _equipConv.TryGetValue( body, out var map ) && map.TryGetValue( animation, out conversion );
    }

    /// <summary>Resolves a body (cached): source file, file body, type, defined actions.</summary>
    public AnimationBody GetBody( int body )
    {
        lock ( _lock )
        {
            if ( _bodies.TryGetValue( body, out var cached ) )
            {
                return cached;
            }
        }

        var resolved = Resolve( body );

        lock ( _lock )
        {
            _bodies[body] = resolved;
        }

        return resolved;
    }

    public bool HasAnimation( int body ) => body is >= 0 and < 0x10000 && GetBody( body ).HasAnimation;

    /// <summary>
    ///     Frames of one action in one of 8 directions: 0..4 are stored (0 faces down the screen,
    ///     4 faces up), 5..7 mirror 3..1. Returns an empty array if the action is not defined.
    ///     The frames are shared (cached); clone a pixmap before modifying it.
    /// </summary>
    public AnimationFrame[] GetFrames( int body, int action, int direction )
    {
        if ( ( uint )action >= MaxActions || ( uint )direction >= Directions || body < 0 )
        {
            return [];
        }

        var key = ( ( long )body << 16 ) | ( ( long )action << 4 ) | ( uint )direction;

        lock ( _lock )
        {
            if ( _frameCache.TryGetValue( key, out var cached ) )
            {
                return cached;
            }
        }

        var info = GetBody( body );
        var frames = info.Source switch
        {
            AnimationSource.MUL => ReadMUL( info, action, direction ),
            AnimationSource.UOP => ReadUOP( info, action, direction ),
            _ => []
        };

        lock ( _lock )
        {
            if ( _frameCache.TryAdd( key, frames ) )
            {
                _frameCacheOrder.Enqueue( key );

                while ( _frameCacheOrder.Count > MaxCachedFrameSets )
                {
                    _frameCache.Remove( _frameCacheOrder.Dequeue() );
                }
            }
        }

        return frames;
    }

    /// <summary>Recolors frames with a game hue (0 = none). Returns new frames; the cached ones are untouched.</summary>
    public static AnimationFrame[] ApplyHue( AnimationFrame[] frames, Hues hues, int hue, bool partial = false )
    {
        if ( hues.Get( hue ) is null )
        {
            return frames;
        }

        var result = new AnimationFrame[frames.Length];

        for ( var i = 0; i < frames.Length; i++ )
        {
            result[i] = frames[i].Image is { } image ? frames[i] with { Image = hues.Apply( image, hue, partial ) } : frames[i];
        }

        return result;
    }

    /// <summary>Union of the frames' extents around their common anchor: (left, top, width, height).</summary>
    public static (int Left, int Top, int Width, int Height) Bounds( IEnumerable<AnimationFrame> frames )
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        foreach ( var f in frames )
        {
            if ( f.IsEmpty )
            {
                continue;
            }

            left = Math.Min( left, f.Left );
            top = Math.Min( top, f.Top );
            right = Math.Max( right, f.Left + f.Width );
            bottom = Math.Max( bottom, f.Top + f.Height );
        }

        return left == int.MaxValue ? ( 0, 0, 0, 0 ) : ( left, top, right - left, bottom - top );
    }

    /// <summary>Draws frames on top of each other at their anchors (e.g. a body plus equipment layers).</summary>
    public static Pixmap? Composite( IReadOnlyList<AnimationFrame> layers, int padding = 0 )
    {
        var (left, top, width, height) = Bounds( layers );

        if ( width == 0 )
        {
            return null;
        }

        var canvas = new Pixmap( width + padding * 2, height + padding * 2 );

        foreach ( var f in layers )
        {
            if ( f.Image is { } image )
            {
                canvas.Blit( image, f.Left - left + padding, f.Top - top + padding );
            }
        }

        return canvas;
    }

    /// <summary>All frames side by side in equal cells, aligned on a shared anchor.</summary>
    public static Pixmap? RenderStrip( IReadOnlyList<AnimationFrame> frames, int gap = 2 )
    {
        var (left, top, width, height) = Bounds( frames );

        if ( width == 0 || frames.Count == 0 )
        {
            return null;
        }

        var strip = new Pixmap( frames.Count * width + ( frames.Count - 1 ) * gap, height );

        for ( var i = 0; i < frames.Count; i++ )
        {
            if ( frames[i].Image is { } image )
            {
                strip.Blit( image, i * ( width + gap ) + frames[i].Left - left, frames[i].Top - top );
            }
        }

        return strip;
    }

    /// <summary>
    ///     Renders an animation made of stacked layers (body, then equipment) into equally sized
    ///     images, one per frame, aligned on the shared anchor so playback does not jitter. Layers
    ///     with fewer frames wrap. The first layer sets the frame count.
    /// </summary>
    public static Pixmap[] RenderLayers( IReadOnlyList<AnimationFrame[]> layers, int padding = 2 )
    {
        if ( layers.Count == 0 || layers[0].Length == 0 )
        {
            return [];
        }

        var (left, top, width, height) = Bounds( layers.SelectMany( l => l ) );

        if ( width == 0 )
        {
            return [];
        }

        var count = layers[0].Length;
        var result = new Pixmap[count];

        for ( var f = 0; f < count; f++ )
        {
            var canvas = new Pixmap( width + padding * 2, height + padding * 2 );

            foreach ( var layer in layers )
            {
                if ( layer.Length > 0 && layer[f % layer.Length] is { Image: { } image } frame )
                {
                    canvas.Blit( image, frame.Left - left + padding, frame.Top - top + padding );
                }
            }

            result[f] = canvas;
        }

        return result;
    }

    /// <summary>
    ///     The name of one action of a body. A UOP (<c>UseUOPAnimation</c>) body with an
    ///     AnimationSequence.uop entry that has mode blocks does not follow the fixed action layout of
    ///     its mobtypes.txt type, so its groups are named from the roles in the mode tables and aliases
    ///     are shown as redirects. Every other body keeps UOFiddler's per-type action names.
    /// </summary>
    public string GetActionName( int body, int action )
    {
        var info = GetBody( body );

        if ( ( info.Flags & MobFlags.UseUOPAnimation ) != 0 && GetSequence( info.FileBody ) is { HasModes: true } sequence )
        {
            return sequence.GetActionName( action );
        }

        return GetActionName( info.Type, action );
    }

    /// <summary>The parsed AnimationSequence.uop entry of a body, or <c>null</c> when it has none.</summary>
    public AnimationSequence? GetSequence( int body )
    {
        UOPArchives();                                   // loads the sequence table on first use
        return _sequences.TryGetValue( body, out var sequence ) ? sequence : null;
    }

    /// <summary>UOFiddler's action names for a body type, or <c>Action N</c>.</summary>
    public static string GetActionName( MobType type, int action )
    {
        var names = type switch
        {
            MobType.Monster => MonsterActions,
            MobType.SeaMonster => SeaActions,
            MobType.Animal => AnimalActions,
            _ => HumanActions
        };

        return ( uint )action < ( uint )names.Length ? names[action] : $"Action {action}";
    }

    private static readonly string[] MonsterActions =
    [
        "Walk", "Idle", "Die1", "Die2", "Attack1", "Attack2", "Attack3", "AttackBow", "AttackCrossBow", "AttackThrow", "GetHit", "Pillage", "Stomp",
        "Cast2", "Cast3", "BlockRight", "BlockLeft", "Idle", "Fidget", "Fly", "TakeOff", "GetHitInAir"
    ];

    private static readonly string[] SeaActions = ["Walk", "Run", "Idle", "Idle", "Fidget", "Attack1", "Attack2", "GetHit", "Die1"];

    private static readonly string[] AnimalActions = ["Walk", "Run", "Idle", "Eat", "Alert", "Attack1", "Attack2", "GetHit", "Die1", "Idle", "Fidget", "LieDown", "Die2"];

    private static readonly string[] HumanActions =
    [
        "Walk_01", "WalkStaff_01", "Run_01", "RunStaff_01", "Idle_01", "Idle_01", "Fidget_Yawn_Stretch_01", "CombatIdle1H_01", "CombatIdle1H_01",
        "AttackSlash1H_01", "AttackPierce1H_01", "AttackBash1H_01", "AttackBash2H_01", "AttackSlash2H_01", "AttackPierce2H_01", "CombatAdvance_1H_01",
        "Spell1", "Spell2", "AttackBow_01", "AttackCrossbow_01", "GetHit_Fr_Hi_01", "Die_Hard_Fwd_01", "Die_Hard_Back_01", "Horse_Walk_01",
        "Horse_Run_01", "Horse_Idle_01", "Horse_Attack1H_SlashRight_01", "Horse_AttackBow_01", "Horse_AttackCrossbow_01", "Horse_Attack2H_SlashRight_01",
        "Block_Shield_Hard_01", "Punch_Punch_Jab_01", "Bow_Lesser_01", "Salute_Armed1h_01", "Ingest_Eat_01"
    ];

    // ---- Resolution -------------------------------------------------------------------------

    private AnimationBody Resolve( int body )
    {
        var requested = body;
        int? bodyDefTarget = null;
        var hue = 0;

        // Body.def can chain; ClassicUO follows it while the body is not converted. Cap the walk.
        for ( var depth = 0; depth < 4; depth++ )
        {
            var hasMobType = _mobTypes.TryGetValue( body, out var mob );

            if ( ( mob.Flags & MobFlags.UseUOPAnimation ) != 0 && HasUOP )
            {
                var actions = UOPActions( body );

                if ( actions.Count > 0 )
                {
                    var type = hasMobType ? mob.Type : TypeByGraphic( body, 0 );
                    return new AnimationBody( requested, type, hasMobType, mob.Flags, AnimationSource.UOP, UOPFileName( body, actions[0] ), body, bodyDefTarget, hue, MaxActions, actions );
                }
            }

            var fileIndex = 0;
            var fileBody = body;

            if ( _bodyConv.TryGetValue( body, out var conv ) )
            {
                ( fileIndex, fileBody ) = conv;
            }
            else if ( _bodyDef.TryGetValue( body, out var def ) && def.Body != body && def.Body >= 0 )
            {
                bodyDefTarget ??= def.Body;
                hue = def.Hue;
                body = def.Body;
                continue;
            }

            var mulType = hasMobType ? mob.Type : TypeByGraphic( fileBody, fileIndex );
            var start = BlockStart( fileBody, mulType, mob.Flags, out var slots );
            var index = MULIndex( fileIndex );
            var list = new List<int>();

            if ( index is not null && start >= 0 && start + slots * StoredDirections <= index.Length )
            {
                for ( var a = 0; a < slots; a++ )
                {
                    if ( index.GetEntry( start + a * StoredDirections ).IsValid )
                    {
                        list.Add( a );
                    }
                }
            }

            return new AnimationBody( requested, mulType, hasMobType, mob.Flags, list.Count > 0 ? AnimationSource.MUL : AnimationSource.None, MULName( fileIndex ), fileBody,
                bodyDefTarget, hue, slots, list );
        }

        return new AnimationBody( requested, MobType.Monster, false, MobFlags.None, AnimationSource.None, "", body, bodyDefTarget, hue, 0, [] );
    }

    private static MobType TypeByGraphic( int graphic, int fileIndex ) => fileIndex switch
    {
        1 => graphic < 200 ? MobType.Monster : MobType.Animal,
        2 => graphic < 300 ? MobType.Animal : graphic < 400 ? MobType.Monster : MobType.Human,
        _ => graphic < 200 ? MobType.Monster : graphic < 400 ? MobType.Animal : MobType.Human
    };

    /// <summary>First idx record of a body and its action slot count (ClassicUO <c>CalculateOffset</c>).</summary>
    private static int BlockStart( int graphic, MobType type, MobFlags flags, out int slots )
    {
        var group = type switch
        {
            MobType.Monster => ( flags & MobFlags.CalculateOffsetByPeopleGroup ) != 0 ? 2 : ( flags & MobFlags.CalculateOffsetByLowGroup ) != 0 ? 0 : 1,
            MobType.SeaMonster => 3,
            MobType.Animal when ( flags & MobFlags.CalculateOffsetLowGroupExtended ) != 0 =>
                ( flags & MobFlags.CalculateOffsetByPeopleGroup ) != 0 ? 2 : ( flags & MobFlags.CalculateOffsetByLowGroup ) != 0 ? 0 : 1,
            MobType.Animal => 0,
            _ => 2
        };

        switch ( group )
        {
            case 0:
                slots = 13;
                return ( graphic - 200 ) * 65 + 22000;
            case 1:
                slots = 22;
                return graphic * 110;
            case 3:
                // Sea monsters sit in the high block but only use the low group's 13 actions.
                slots = 13;
                return graphic * 110;
            default:
                slots = 35;
                return ( graphic - 400 ) * 175 + 35000;
        }
    }

    private static string MULName( int fileIndex ) => fileIndex == 0 ? "anim.mul" : $"anim{fileIndex + 1}.mul";

    private static string IdxName( int fileIndex ) => fileIndex == 0 ? "anim.idx" : $"anim{fileIndex + 1}.idx";

    private FileIndex? MULIndex( int fileIndex )
    {
        if ( ( uint )fileIndex >= _mul.Length )
        {
            return null;
        }

        lock ( _lock )
        {
            if ( !_mulOpened[fileIndex] )
            {
                _mulOpened[fileIndex] = true;

                if ( Files.Find( IdxName( fileIndex ) ) is { } idx && Files.Find( MULName( fileIndex ) ) is { } mul )
                {
                    _mul[fileIndex] = FileIndex.OpenMUL( idx, mul );
                }
            }

            return _mul[fileIndex];
        }
    }

    // ---- MUL frames -------------------------------------------------------------------------

    private AnimationFrame[] ReadMUL( AnimationBody info, int action, int direction )
    {
        if ( action >= info.ActionSlots || !info.IsDefined( action ) )
        {
            return [];
        }

        var fileIndex = Array.FindIndex( [0, 1, 2, 3, 4, 5], i => MULName( i ) == info.FileName );
        var index = MULIndex( fileIndex );
        var start = BlockStart( info.FileBody, info.Type, info.Flags, out _ );
        var stored = direction <= 4 ? direction : 8 - direction;

        if ( index is null || !index.TryRead( start + action * StoredDirections + stored, out var data ) )
        {
            return [];
        }

        return DecodeMUL( data, direction > 4 );
    }

    /// <summary>
    ///     A MUL animation block: a 256-color palette, a frame count and offsets (relative to the
    ///     count), then each frame.
    /// </summary>
    public static AnimationFrame[] DecodeMUL( ReadOnlySpan<byte> data, bool flip )
    {
        if ( data.Length < 516 )
        {
            return [];
        }

        var palette = data[..512];
        var count = BinaryPrimitives.ReadInt32LittleEndian( data[512..] );

        if ( count <= 0 || count > 1024 || 516 + count * 4 > data.Length )
        {
            return [];
        }

        var frames = new AnimationFrame[count];

        for ( var i = 0; i < count; i++ )
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian( data[( 516 + i * 4 )..] );
            frames[i] = DecodeFrame( data, 512 + offset, palette, false, flip );
        }

        return frames;
    }

    /// <summary>
    ///     One sprite: i16 centerX, i16 centerY, u16 width, u16 height, then runs headed by a u32
    ///     (10-bit signed x, 10-bit signed y relative to the center, 12-bit length) followed by
    ///     palette indices, ended by 0x7FFF7FFF.
    /// </summary>
    private static AnimationFrame DecodeFrame( ReadOnlySpan<byte> data, int pos, ReadOnlySpan<byte> palette, bool uop, bool flip )
    {
        if ( pos < 0 || pos + 8 > data.Length )
        {
            return default;
        }

        int cx = BinaryPrimitives.ReadInt16LittleEndian( data[pos..] );
        int cy = BinaryPrimitives.ReadInt16LittleEndian( data[( pos + 2 )..] );
        int width = BinaryPrimitives.ReadUInt16LittleEndian( data[( pos + 4 )..] );
        int height = BinaryPrimitives.ReadUInt16LittleEndian( data[( pos + 6 )..] );

        if ( width == 0 || height == 0 || width > 2048 || height > 2048 )
        {
            return new AnimationFrame( null, flip ? -cx : cx, cy );
        }

        var pixmap = new Pixmap( width, height );
        var pixels = pixmap.Pixels;
        var p = pos + 8;

        while ( p + 4 <= data.Length )
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian( data[p..] );
            p += 4;

            if ( header == 0x7FFF7FFF )
            {
                break;
            }

            var run = ( int )( header & 0xFFF );
            var x = ( int )( ( header >> 22 ) & 0x3FF );
            var y = ( int )( ( header >> 12 ) & 0x3FF );

            if ( ( x & 0x200 ) != 0 )
            {
                x |= unchecked( ( int )0xFFFFFC00 );
            }

            if ( ( y & 0x200 ) != 0 )
            {
                y |= unchecked( ( int )0xFFFFFC00 );
            }

            x += cx;
            y += cy + height;

            var end = Math.Min( p + run, data.Length );
            var rowOk = ( uint )y < ( uint )height;

            for ( ; p < end; p++, x++ )
            {
                if ( !rowOk || ( uint )x >= ( uint )width )
                {
                    continue;
                }

                var color = BinaryPrimitives.ReadUInt16LittleEndian( palette[( data[p] * 2 )..] );
                var argb = uop ? Color16.ToArgbTransparent( color ) : Color16.ToArgb( color );
                pixels[y * width + ( flip ? width - 1 - x : x )] = argb;
            }
        }

        return new AnimationFrame( pixmap, flip ? width - cx : cx, cy );
    }

    // ---- UOP frames -------------------------------------------------------------------------

    private UOPArchive[] UOPArchives()
    {
        lock ( _lock )
        {
            if ( _uop is not null )
            {
                return _uop;
            }

            var list = new List<UOPArchive>();

            for ( var i = 1; i <= 10; i++ )
            {
                if ( Files.Find( $"AnimationFrame{i}.uop" ) is { } path )
                {
                    list.Add( UOPArchive.Open( path ) );
                }
            }

            if ( Files.Find( "AnimationSequence.uop" ) is { } sequence )
            {
                ReadAnimationSequence( sequence );
            }

            _uop = list.ToArray();
            return _uop;
        }
    }

    private int ResolveUOPAction( int body, int action )
    {
        var sequence = GetSequence( body );
        return sequence is not null && ( uint )action < ( uint )sequence.Replaced.Count ? sequence.Replaced[action] : action;
    }

    private bool TryFindUOP( int body, int action, out UOPArchive archive, out UOPEntry entry )
    {
        var hash = UOPHashFor( body, action );

        foreach ( var a in UOPArchives() )
        {
            if ( a.TryGet( hash, out entry ) )
            {
                archive = a;
                return true;
            }
        }

        archive = null!;
        entry = default;
        return false;
    }

    private static ulong UOPHashFor( int body, int action )
    {
        Span<char> buffer = stackalloc char[64];
        buffer.TryWrite( CultureInfo.InvariantCulture, UOPFramePattern, out var written, body, action );
        return UOPHash.Compute( buffer[..written] );
    }

    private List<int> UOPActions( int body )
    {
        var list = new List<int>();

        for ( var a = 0; a < MaxActions; a++ )
        {
            if ( TryFindUOP( body, ResolveUOPAction( body, a ), out _, out _ ) )
            {
                list.Add( a );
            }
        }

        return list;
    }

    private string UOPFileName( int body, int action )
    {
        return TryFindUOP( body, ResolveUOPAction( body, action ), out var archive, out _ ) ? Path.GetFileName( archive.File.Path ) : "AnimationFrame*.uop";
    }

    private AnimationFrame[] ReadUOP( AnimationBody info, int action, int direction )
    {
        if ( !info.IsDefined( action ) || !TryFindUOP( info.FileBody, ResolveUOPAction( info.FileBody, action ), out var archive, out var entry ) )
        {
            return [];
        }

        var data = archive.Read( entry );
        var perDirection = info.Type < MobType.Equipment ? -1 : StoredDirections * 2;
        return DecodeUOP( data, direction <= 4 ? direction : 8 - direction, direction > 4, perDirection );
    }

    /// <summary>
    ///     An AnimationFrame*.uop group: a 32-byte header, frame count, offset of 16-byte frame
    ///     headers (group, 1-based frame id, 8 unknown bytes, data offset relative to the header).
    ///     Frame ids run through the directions (dir = (id - 1) / framesPerDirection); missing ids
    ///     are missing frames. Each frame's data is a 256-color palette and a sprite.
    /// </summary>
    public static AnimationFrame[] DecodeUOP( ReadOnlySpan<byte> data, int storedDirection, bool flip, int framesPerDirection = -1 )
    {
        if ( data.Length < 40 )
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian( data[32..] );
        var start = BinaryPrimitives.ReadInt32LittleEndian( data[36..] );

        if ( count <= 0 || start < 0 || start + count * 16L > data.Length )
        {
            return [];
        }

        var perDirection = framesPerDirection > 0 ? framesPerDirection : ( int )Math.Round( count / ( float )StoredDirections );

        if ( perDirection <= 0 )
        {
            return [];
        }

        var frames = new AnimationFrame[perDirection];
        var last = -1;

        for ( var i = 0; i < count; i++ )
        {
            var header = start + i * 16;
            var frameId = BinaryPrimitives.ReadUInt16LittleEndian( data[( header + 2 )..] ) - 1;

            if ( frameId < 0 || frameId / perDirection != storedDirection )
            {
                continue;
            }

            var num = frameId % perDirection;
            var offset = header + ( int )BinaryPrimitives.ReadUInt32LittleEndian( data[( header + 12 )..] );

            if ( offset < 0 || offset + 512 > data.Length )
            {
                continue;
            }

            frames[num] = DecodeFrame( data, offset + 512, data.Slice( offset, 512 ), true, flip );
            last = Math.Max( last, num );
        }

        return last < 0 ? [] : frames;
    }

    /// <summary>
    ///     AnimationSequence.uop. The client finds an entry by its name
    ///     <c>build/animationsequence/{0:D8}.bin</c>; a few shipped entries hold a different body id
    ///     inside, so entries are keyed by the name and never by that field. Each entry is parsed in
    ///     full (action records + mode blocks) by <see cref="AnimationSequence" />.
    /// </summary>
    private void ReadAnimationSequence( string path )
    {
        using var archive = UOPArchive.Open( path );
        var remaining = archive.Count;

        // Body ids are 16-bit in the client; stop early once every named entry has been found.
        for ( var body = 0; body <= 0xFFFF && remaining > 0; body++ )
        {
            if ( !archive.TryGet( UOPSequencePattern, body, out var entry ) )
            {
                continue;
            }

            remaining--;

            byte[] data;

            try
            {
                data = archive.Read( entry ).ToArray();
            }
            catch ( InvalidDataException )
            {
                continue;
            }

            if ( AnimationSequence.Parse( data ) is { } sequence )
            {
                _sequences[body] = sequence;
            }
        }
    }

    // ---- Definition files -------------------------------------------------------------------

    private IEnumerable<string> DefLines( string name )
    {
        if ( Files.Find( name ) is not { } path )
        {
            yield break;
        }

        foreach ( var raw in File.ReadLines( path ) )
        {
            var line = raw.Trim();

            if ( line.Length == 0 || line[0] == '#' || line[0] == '"' )
            {
                continue;
            }

            var comment = line.IndexOf( '#' );
            yield return comment > 0 ? line[..comment].Trim() : line;
        }
    }

    private static readonly char[] Separators = [' ', '\t'];

    private void ReadMobTypes()
    {
        foreach ( var line in DefLines( "mobtypes.txt" ) )
        {
            var parts = line.Split( Separators, StringSplitOptions.RemoveEmptyEntries );

            if ( parts.Length < 3 || !int.TryParse( parts[0], CultureInfo.InvariantCulture, out var id ) ||
                 !uint.TryParse( parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags ) )
            {
                continue;
            }

            MobType? type = parts[1].ToLowerInvariant() switch
            {
                "monster" => MobType.Monster,
                "sea_monster" => MobType.SeaMonster,
                "animal" => MobType.Animal,
                "human" => MobType.Human,
                "equipment" => MobType.Equipment,
                _ => null
            };

            if ( type is { } t )
            {
                _mobTypes[id] = ( t, ( MobFlags )flags );
            }
        }
    }

    /// <summary><c>body {target[, ...]} hue</c>; with 3+ targets the third is used (as the client does).</summary>
    private void ReadBodyDef( string name, Dictionary<int, (int Body, int Hue)> table )
    {
        foreach ( var line in DefLines( name ) )
        {
            var open = line.IndexOf( '{' );
            var close = line.IndexOf( '}' );

            if ( open <= 0 || close < open || !int.TryParse( line.AsSpan( 0, open ).Trim(), CultureInfo.InvariantCulture, out var id ) )
            {
                continue;
            }

            var targets = line[( open + 1 )..close].Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

            if ( targets.Length == 0 || !int.TryParse( targets[targets.Length >= 3 ? 2 : 0], CultureInfo.InvariantCulture, out var target ) )
            {
                continue;
            }

            _ = int.TryParse( line.AsSpan( close + 1 ).Trim(), CultureInfo.InvariantCulture, out var hue );

            if ( target >= 0 )
            {
                table.TryAdd( id, ( target, hue ) );
            }
        }
    }

    /// <summary>Server body followed by its body in anim2..anim6 (-1 = none); the last valid column wins, as in ClassicUO.</summary>
    private void ReadBodyConv()
    {
        foreach ( var line in DefLines( "Bodyconv.def" ) )
        {
            var parts = line.Split( Separators, StringSplitOptions.RemoveEmptyEntries );

            if ( parts.Length < 2 || !int.TryParse( parts[0], CultureInfo.InvariantCulture, out var id ) )
            {
                continue;
            }

            for ( var column = 1; column < parts.Length && column < 6; column++ )
            {
                if ( int.TryParse( parts[column], CultureInfo.InvariantCulture, out var body ) && body >= 0 &&
                     Files.Exists( MULName( column ) ) && Files.Exists( IdxName( column ) ) )
                {
                    _bodyConv[id] = ( column, body );
                }
            }
        }
    }

    /// <summary><c>body animation newAnimation gump hue</c>; gump 0 = animation + 50000, -1 = newAnimation + 50000.</summary>
    private void ReadEquipConv()
    {
        foreach ( var line in DefLines( "Equipconv.def" ) )
        {
            var parts = line.Split( Separators, StringSplitOptions.RemoveEmptyEntries );

            if ( parts.Length < 5 ||
                 !int.TryParse( parts[0], CultureInfo.InvariantCulture, out var body ) ||
                 !int.TryParse( parts[1], CultureInfo.InvariantCulture, out var animation ) ||
                 !int.TryParse( parts[2], CultureInfo.InvariantCulture, out var converted ) ||
                 !int.TryParse( parts[3], CultureInfo.InvariantCulture, out var gump ) ||
                 !int.TryParse( parts[4], CultureInfo.InvariantCulture, out var hue ) )
            {
                continue;
            }

            gump = gump switch
            {
                0 => animation + 50000,
                -1 or 0xFFFF => converted + 50000,
                _ => gump
            };

            if ( !_equipConv.TryGetValue( body, out var map ) )
            {
                _equipConv[body] = map = new Dictionary<int, EquipConversion>();
            }

            map[animation] = new EquipConversion( converted, gump, hue );
        }
    }
}
