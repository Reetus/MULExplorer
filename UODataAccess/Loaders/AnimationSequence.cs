using System.Buffers.Binary;

namespace UODataAccess.Loaders;

/// <summary>One action (frameset) record of an <c>AnimationSequence.uop</c> entry.</summary>
public readonly record struct SequenceAction( int Group, int Frames, int Alias )
{
    /// <summary><c>Alias == -1</c> means the group has frames of its own; otherwise it borrows that group's.</summary>
    public bool IsReal => Alias < 0;

    public bool IsAlias => Alias >= 0;
}

/// <summary>A movement table row: <paramref name="Code"/> 0 stand, 1 walk, 2 run.</summary>
public readonly record struct SequenceMovement( int Code, int Action, int StaffAction );

/// <summary>An event table row: a list of actions indexed by the 0xE2 phase byte.</summary>
public sealed record SequenceEvent( int Event, int Sub, IReadOnlyList<int> Actions );

/// <summary>One mode block (combat, peace, mounted, ...): what a body plays in a state.</summary>
public sealed record SequenceMode(
    int Id,
    int Parent,
    int DefaultAction,
    IReadOnlyList<SequenceMovement> Movements,
    IReadOnlyList<SequenceEvent> Events )
{
    /// <summary>Parent marker meaning "no parent".</summary>
    public const int NoParent = 0xFF;

    /// <summary>Sub-action marker meaning "any".</summary>
    public const int AnySub = 0xFF;
}

/// <summary>
///     One parsed <c>AnimationSequence.uop</c> entry: the action records and the mode blocks that
///     give each group its role (stand, walk, die, ...). The original client takes every action of a
///     UOP body from this entry instead of the fixed per-type tables, so this is also the source of
///     the action names shown for such bodies.
/// </summary>
/// <remarks>
///     Layout (little-endian): <c>u32 internal id</c>, 48 bytes, <c>u32 record count</c>, then one
///     64-byte record per action (<c>u32 group, u32 frames, i32 alias, f32 fps</c>, 48 bytes) followed
///     by two counted lists (<c>u32 n + n*32</c>, <c>u32 n + n*4</c>). After the records, <c>u32</c>
///     mode count and one block per mode: <c>u8 id, u8 parent, u32 default</c>, then counted tables of
///     5-byte transitions (unused), 9-byte movements and event lists (<c>u8 event, u8 sub, u32 n,
///     n*u32 actions</c>). The first block of a duplicated mode or group wins; data that ends inside a
///     record keeps the records read so far and has no modes.
/// </remarks>
public sealed class AnimationSequence
{
    private const int MovementKinds = 3;
    private const int EventKinds = 16;
    private const int Kinds = MovementKinds + EventKinds;

    // The role words live here, next to the static action tables in Animations.cs: they are data
    // descriptions shared by the MCP tool and the UI, and MULExplorer.Mcp cannot reference the UI resx.
    private static readonly string[] MovementNames = ["Stand", "Walk", "Run"];

    private static readonly string[] EventNames =
    [
        "Attack", "Parry", "Block", "Die", "Impact", "Fidget", "Eat", "Emote", "Alert", "TakeOff", "Land", "Spell",
        "StartCombat", "EndCombat", "Pillage", "Spawn"
    ];

    private static readonly string[] ModeNames = ["combat", "peace", "mounted", "fly", "fly war", "fly peace", "sitting"];

    private readonly SequenceMode[] _modes;
    private readonly Dictionary<int, SequenceMode> _modeById = new();
    private readonly string[] _actionNames;

    private AnimationSequence( int internalId, SequenceAction[] actions, SequenceMode[] modes )
    {
        InternalId = internalId;
        Actions = actions;
        _modes = modes;

        foreach ( var mode in _modes )
        {
            _modeById.TryAdd( mode.Id, mode );
        }

        Replaced = ResolveAliases( actions );
        _actionNames = BuildActionNames( actions, _modes );
    }

    /// <summary>The id stored at offset 0 of the entry. Deliberately <b>not</b> used as the key.</summary>
    public int InternalId { get; }

    /// <summary>The action records, in file order (duplicates removed, first wins).</summary>
    public IReadOnlyList<SequenceAction> Actions { get; }

    /// <summary>The mode blocks, in file order (duplicates removed, first wins).</summary>
    public IReadOnlyList<SequenceMode> Modes => _modes;

    /// <summary>Whether the entry has any mode block (all shipped entries do).</summary>
    public bool HasModes => _modes.Length > 0;

    /// <summary>Action -&gt; the group whose frames it uses: one redirect, else the lowest real group.</summary>
    public IReadOnlyList<int> Replaced { get; }

    /// <summary>
    ///     The label of one group: <c>-&gt; N</c> for an alias, the roles from the mode tables for a
    ///     real group, or <c>Action N</c> when no role points at it.
    /// </summary>
    public string GetActionName( int action ) => ( uint )action < ( uint )_actionNames.Length ? _actionNames[action] : $"Action {action}";

    /// <summary>
    ///     FUN_00408114: the action for a movement (0 stand, 1 walk, 2 run) in a mode. A movement
    ///     missing from a mode is taken from its parent, then from the default action of the last
    ///     mode asked. Returns -1 when the mode does not exist.
    /// </summary>
    public int Movement( int mode, int code )
    {
        SequenceMode? last = null;

        foreach ( var m in Chain( mode ) )
        {
            foreach ( var movement in m.Movements )
            {
                if ( movement.Code == code )
                {
                    return movement.Action;
                }
            }

            last = m;
        }

        return last?.DefaultAction ?? -1;
    }

    /// <summary>
    ///     FUN_00408069: the action for a 0xE2 event in a mode. The list for <paramref name="sub"/>
    ///     comes before the one for any sub-action; empty lists are missing. Missing entries fall back
    ///     through the parents to the last mode's default. <paramref name="phase"/> picks within the
    ///     list. Returns -1 when the mode does not exist.
    /// </summary>
    public int Event( int mode, int eventId, int sub, int phase = 0 )
    {
        SequenceMode? last = null;

        foreach ( var m in Chain( mode ) )
        {
            var actions = FindEvent( m, eventId, sub ) ?? FindEvent( m, eventId, SequenceMode.AnySub );

            if ( actions is { Count: > 0 } )
            {
                var index = ( ( phase % actions.Count ) + actions.Count ) % actions.Count;
                return actions[index];
            }

            last = m;
        }

        return last?.DefaultAction ?? -1;
    }

    /// <summary>
    ///     Parses one decompressed entry. Returns <c>null</c> only when the blob is too short for its
    ///     header; truncated records/modes keep whatever could be read before the cut.
    /// </summary>
    public static AnimationSequence? Parse( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 56 )
        {
            return null;
        }

        var internalId = BinaryPrimitives.ReadInt32LittleEndian( data );
        var count = BinaryPrimitives.ReadUInt32LittleEndian( data[52..] );
        var reader = new Reader( data, 56 );
        var actions = new List<SequenceAction>();
        var seenGroups = new HashSet<int>();
        var complete = true;

        for ( uint g = 0; g < count; g++ )
        {
            var group = reader.I32();
            var frames = reader.I32();
            var alias = reader.I32();
            reader.Skip( 52 );                       // fps + 48 bytes
            var sounds = reader.U32();
            reader.Skip( ( long )sounds * 32 );
            var set = reader.U32();
            reader.Skip( ( long )set * 4 );

            if ( reader.Failed )
            {
                complete = false;
                break;
            }

            if ( seenGroups.Add( group ) )
            {
                actions.Add( new SequenceAction( group, frames, alias ) );
            }
        }

        var modes = new List<SequenceMode>();

        if ( complete )
        {
            var modeCount = reader.U32();
            var seenModes = new HashSet<int>();

            for ( uint m = 0; m < modeCount && !reader.Failed; m++ )
            {
                if ( !reader.TryReadMode( out var mode ) )
                {
                    break;
                }

                if ( seenModes.Add( mode.Id ) )
                {
                    modes.Add( mode );
                }
            }
        }

        return new AnimationSequence( internalId, actions.ToArray(), modes.ToArray() );
    }

    /// <summary>
    ///     The client follows one alias redirect: a target that is itself an alias, or has no record,
    ///     falls back to the body's lowest-numbered real group. Real groups and actions with no record
    ///     keep their own frames.
    /// </summary>
    private static int[] ResolveAliases( SequenceAction[] actions )
    {
        var aliasOf = new Dictionary<int, int>();

        foreach ( var action in actions )
        {
            aliasOf.TryAdd( action.Group, action.Alias );
        }

        var lowestReal = -1;

        foreach ( var (group, alias) in aliasOf )
        {
            if ( alias < 0 && ( lowestReal < 0 || group < lowestReal ) )
            {
                lowestReal = group;
            }
        }

        var replaced = new int[Animations.MaxActions];

        for ( var i = 0; i < replaced.Length; i++ )
        {
            if ( !aliasOf.TryGetValue( i, out var alias ) || alias < 0 )
            {
                replaced[i] = i;
                continue;
            }

            // Accept the redirect only when its target has a real record; otherwise use the lowest real group.
            replaced[i] = aliasOf.TryGetValue( alias, out var targetAlias ) && targetAlias < 0
                ? alias
                : lowestReal >= 0 ? lowestReal : i;
        }

        return replaced;
    }

    /// <summary>
    ///     Names every group from the roles the mode tables give it. Movements are tied to a mode, so
    ///     they are qualified with the mode name; an event is qualified only when it is defined in more
    ///     than one mode (only combat -&gt; "Die", combat + peace -&gt; "Fidget (peace)"). A role kind
    ///     counts a mode only when it has a non-empty explicit entry there. A group with several roles
    ///     shows them all, ordered by role kind then mode. Aliases and unused groups are handled first.
    /// </summary>
    private static string[] BuildActionNames( SequenceAction[] actions, SequenceMode[] modes )
    {
        var modesByKind = new HashSet<int>[Kinds];
        var roles = new List<(int Group, int Kind, int Mode)>();

        foreach ( var mode in modes )
        {
            foreach ( var movement in mode.Movements )
            {
                if ( ( uint )movement.Code >= MovementKinds )
                {
                    continue;
                }

                ( modesByKind[movement.Code] ??= new HashSet<int>() ).Add( mode.Id );
                roles.Add( ( movement.Action, movement.Code, mode.Id ) );
            }

            foreach ( var sequenceEvent in mode.Events )
            {
                if ( ( uint )sequenceEvent.Event >= EventKinds || sequenceEvent.Actions.Count == 0 )
                {
                    continue;                                   // empty lists are missing
                }

                var kind = MovementKinds + sequenceEvent.Event;
                ( modesByKind[kind] ??= new HashSet<int>() ).Add( mode.Id );

                foreach ( var action in sequenceEvent.Actions.Distinct() )
                {
                    roles.Add( ( action, kind, mode.Id ) );
                }
            }
        }

        var qualify = new bool[Kinds];

        for ( var k = 0; k < Kinds; k++ )
        {
            qualify[k] = modesByKind[k] is { Count: > 1 };
        }

        var byGroup = new Dictionary<int, List<(int Kind, int Mode)>>();

        foreach ( var (group, kind, mode) in roles )
        {
            if ( !byGroup.TryGetValue( group, out var list ) )
            {
                byGroup[group] = list = new List<(int Kind, int Mode)>();
            }

            list.Add( ( kind, mode ) );
        }

        var aliasOf = new Dictionary<int, int>();

        foreach ( var action in actions )
        {
            if ( action.IsAlias )
            {
                aliasOf.TryAdd( action.Group, action.Alias );
            }
        }

        var names = new string[Animations.MaxActions];

        for ( var i = 0; i < names.Length; i++ )
        {
            if ( aliasOf.TryGetValue( i, out var target ) )
            {
                names[i] = $"-> {target}";
                continue;
            }

            names[i] = byGroup.TryGetValue( i, out var groupRoles ) && groupRoles.Count > 0
                ? string.Join( ", ", groupRoles.Distinct()
                    .OrderBy( r => r.Kind )
                    .ThenBy( r => r.Mode )
                    .Select( r => qualify[r.Kind] ? $"{KindName( r.Kind )} ({ModeName( r.Mode )})" : KindName( r.Kind ) )
                    .Distinct() )
                : $"Action {i}";
        }

        return names;
    }

    private static string KindName( int kind ) => kind < MovementKinds ? MovementNames[kind] : EventNames[kind - MovementKinds];

    private static string ModeName( int mode ) => ( uint )mode < ( uint )ModeNames.Length ? ModeNames[mode] : $"mode {mode}";

    private static IReadOnlyList<int>? FindEvent( SequenceMode mode, int eventId, int sub )
    {
        foreach ( var sequenceEvent in mode.Events )
        {
            if ( sequenceEvent.Event == eventId && sequenceEvent.Sub == sub && sequenceEvent.Actions.Count > 0 )
            {
                return sequenceEvent.Actions;
            }
        }

        return null;
    }

    private IEnumerable<SequenceMode> Chain( int mode )
    {
        var current = _modeById.GetValueOrDefault( mode );
        var guard = _modes.Length;

        while ( current is not null && guard-- > 0 )
        {
            yield return current;
            current = current.Parent == SequenceMode.NoParent ? null : _modeById.GetValueOrDefault( current.Parent );
        }
    }

    /// <summary>Bounds-checked cursor over the entry; once <see cref="Failed"/> is set all reads stop.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _p;

        public Reader( ReadOnlySpan<byte> data, int start )
        {
            _data = data;
            _p = start;
        }

        public bool Failed { get; private set; }

        private int Remaining => _data.Length - _p;

        public byte U8()
        {
            if ( Failed || Remaining < 1 )
            {
                Failed = true;
                return 0;
            }

            return _data[_p++];
        }

        public int I32()
        {
            if ( Failed || Remaining < 4 )
            {
                Failed = true;
                return 0;
            }

            var value = BinaryPrimitives.ReadInt32LittleEndian( _data[_p..] );
            _p += 4;
            return value;
        }

        public uint U32()
        {
            if ( Failed || Remaining < 4 )
            {
                Failed = true;
                return 0;
            }

            var value = BinaryPrimitives.ReadUInt32LittleEndian( _data[_p..] );
            _p += 4;
            return value;
        }

        public void Skip( long count )
        {
            if ( Failed || count < 0 || count > Remaining )
            {
                Failed = true;
                return;
            }

            _p += ( int )count;
        }

        public bool TryReadMode( out SequenceMode mode )
        {
            mode = null!;
            var id = U8();
            var parent = U8();
            var defaultAction = I32();

            var transitions = U32();
            Skip( ( long )transitions * 5 );                 // unused by the client

            var movementCount = U32();
            var movements = new List<SequenceMovement>();

            for ( uint k = 0; k < movementCount; k++ )
            {
                var code = U8();
                var action = I32();
                var staffAction = I32();

                if ( Failed )
                {
                    return false;
                }

                movements.Add( new SequenceMovement( code, action, staffAction ) );
            }

            var eventCount = U32();
            var events = new List<SequenceEvent>();

            for ( uint k = 0; k < eventCount; k++ )
            {
                var eventId = U8();
                var sub = U8();
                var n = U32();

                if ( Failed || n > ( uint )( Remaining / 4 ) )
                {
                    Failed = true;
                    return false;
                }

                var list = new int[( int )n];

                for ( uint j = 0; j < n; j++ )
                {
                    list[j] = I32();
                }

                if ( Failed )
                {
                    return false;
                }

                events.Add( new SequenceEvent( eventId, sub, list ) );
            }

            if ( Failed )
            {
                return false;
            }

            mode = new SequenceMode( id, parent, defaultAction, movements, events );
            return true;
        }
    }
}
