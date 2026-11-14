using System.Text.Json.Nodes;
using MULExplorer.Mcp.Tools;
using UODataAccess.IO;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>
///     Synthetic-format tests for the AnimationSequence.uop reader plus the real-client 0x610 test.
///     The synthetic entries are built from the format verified against both shipped clients.
/// </summary>
public sealed class AnimationSequenceTests
{
    private const int Any = SequenceMode.AnySub;
    private const int NoParent = SequenceMode.NoParent;

    private sealed record TestMode(
        int Id,
        int Parent,
        int Default,
        (int Code, int Action, int Staff)[] Movements,
        (int Event, int Sub, int[] Actions)[] Events );

    /// <summary>Builds one decompressed entry: header, records (64 fixed bytes + two counted lists), modes.</summary>
    private static byte[] BuildSequence(
        int internalId,
        IReadOnlyList<(int Group, int Frames, int Alias, int Extra)> actions,
        IReadOnlyList<TestMode> modes )
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter( ms );

        w.Write( internalId );
        w.Write( new byte[48] );
        w.Write( actions.Count );

        foreach ( var a in actions )
        {
            w.Write( a.Group );
            w.Write( a.Frames );
            w.Write( a.Alias );
            w.Write( 0f );                         // fps
            w.Write( new byte[48] );               // offset block
            w.Write( a.Extra );                    // sounds: n * 32
            w.Write( new byte[a.Extra * 32] );
            w.Write( a.Extra );                    // set: n * 4
            w.Write( new byte[a.Extra * 4] );
        }

        w.Write( modes.Count );

        foreach ( var m in modes )
        {
            w.Write( ( byte )m.Id );
            w.Write( ( byte )m.Parent );
            w.Write( m.Default );
            w.Write( 0 );                          // transitions (unused)
            w.Write( m.Movements.Length );

            foreach ( var (code, action, staff) in m.Movements )
            {
                w.Write( ( byte )code );
                w.Write( action );
                w.Write( staff );
            }

            w.Write( m.Events.Length );

            foreach ( var (eventId, sub, list) in m.Events )
            {
                w.Write( ( byte )eventId );
                w.Write( ( byte )sub );
                w.Write( list.Length );

                foreach ( var action in list )
                {
                    w.Write( action );
                }
            }
        }

        return ms.ToArray();
    }

    /// <summary>The 0x610 tables, in miniature: 3 modes, the rottweiler's aliases and empty spell list.</summary>
    private static byte[] RottweilerLike()
    {
        (int, int, int, int)[] actions =
        [
            ( 0, 10, -1, 0 ), ( 1, 10, -1, 0 ), ( 2, 10, -1, 0 ), ( 3, 0, 2, 0 ), ( 4, 7, -1, 0 ),
            ( 5, 0, 4, 0 ), ( 6, 0, 4, 0 ), ( 7, 0, 4, 0 ), ( 8, 0, 4, 0 ), ( 9, 0, 4, 0 ),
            ( 10, 8, -1, 0 ), ( 11, 0, 25, 0 ), ( 12, 0, 4, 0 ), ( 13, 0, 4, 0 ), ( 14, 0, 4, 0 ),
            ( 15, 7, -1, 0 ), ( 16, 0, 15, 0 ), ( 17, 0, 1, 0 ), ( 18, 0, 1, 0 ), ( 19, 0, 0, 0 ),
            ( 20, 0, 1, 0 ), ( 21, 0, 1, 0 ), ( 22, 10, -1, 0 ), ( 23, 0, 25, 0 ), ( 24, 10, -1, 0 ),
            ( 25, 10, -1, 0 ), ( 26, 21, -1, 0 ), ( 27, 11, -1, 0 ), ( 28, 7, -1, 0 ),
            ( 29, 10, -1, 0 ), ( 30, 10, -1, 0 ), ( 31, 10, -1, 0 )
        ];

        TestMode[] modes =
        [
            new( 0, NoParent, 1,
                [ ( 0, 1, 1 ), ( 1, 0, 0 ), ( 2, 24, 24 ) ],
                [ ( 0, Any, [4] ), ( 2, Any, [15] ), ( 3, Any, [2] ), ( 4, Any, [10] ), ( 5, Any, [27] ),
                  ( 8, Any, [28] ), ( 11, Any, [] ), ( 14, Any, [25] ), ( 15, Any, [] ) ] ),
            new( 1, 0, 25,
                [ ( 0, 25, 25 ), ( 1, 22, 22 ), ( 2, 24, 24 ) ],
                [ ( 5, Any, [26] ) ] ),
            new( 2, 1, 31,
                [ ( 0, 31, 31 ), ( 1, 29, 29 ), ( 2, 30, 30 ) ],
                [ ( 5, Any, [31] ) ] )
        ];

        return BuildSequence( 1552, actions, modes );
    }

    [Fact]
    public void Parse_RottweilerLike_RecordsModesLookupsAndNames()
    {
        var sequence = AnimationSequence.Parse( RottweilerLike() )!;

        Assert.NotNull( sequence );
        Assert.Equal( 1552, sequence.InternalId );
        Assert.Equal( 32, sequence.Actions.Count );
        Assert.True( sequence.HasModes );
        Assert.Equal( 3, sequence.Modes.Count );
        Assert.Equal( new[] { 0, 1, 2 }, sequence.Modes.Select( m => m.Id ).ToArray() );
        Assert.Equal( new[] { NoParent, 0, 1 }, sequence.Modes.Select( m => m.Parent ).ToArray() );

        // Records: the real groups and the aliases from the brief.
        Assert.Equal( 2, sequence.Actions.Single( a => a.Group == 3 ).Alias );
        Assert.True( sequence.Actions.Single( a => a.Group == 2 ).IsReal );
        Assert.Equal( 4, sequence.Actions.Single( a => a.Group == 9 ).Alias );

        // One redirect to a real group (all rottweiler aliases are single-level).
        Assert.Equal( 2, sequence.Replaced[3] );
        Assert.Equal( 4, sequence.Replaced[5] );
        Assert.Equal( 25, sequence.Replaced[11] );

        // Movement table, resolved through parents (peace/mounted have no die event; combat has one).
        Assert.Equal( ( 1, 0, 24 ), ( sequence.Movement( 0, 0 ), sequence.Movement( 0, 1 ), sequence.Movement( 0, 2 ) ) );
        Assert.Equal( ( 25, 22, 24 ), ( sequence.Movement( 1, 0 ), sequence.Movement( 1, 1 ), sequence.Movement( 1, 2 ) ) );
        Assert.Equal( ( 31, 29, 30 ), ( sequence.Movement( 2, 0 ), sequence.Movement( 2, 1 ), sequence.Movement( 2, 2 ) ) );
        Assert.Equal( 1, sequence.Movement( 0, 5 ) );                 // no such code -> root default
        Assert.Equal( -1, sequence.Movement( 7, 0 ) );                // no such mode

        // Events: explicit, via parents, and empty list -> root default.
        Assert.Equal( 2, sequence.Event( 0, 3, Any ) );
        Assert.Equal( 2, sequence.Event( 1, 3, Any ) );                // peace die -> parent combat
        Assert.Equal( 2, sequence.Event( 2, 3, Any ) );                // mounted die -> parents
        Assert.Equal( 27, sequence.Event( 0, 5, Any ) );
        Assert.Equal( 26, sequence.Event( 1, 5, Any ) );
        Assert.Equal( 31, sequence.Event( 2, 5, Any ) );
        Assert.Equal( 4, sequence.Event( 0, 0, Any ) );
        Assert.Equal( 10, sequence.Event( 0, 4, Any ) );
        Assert.Equal( 15, sequence.Event( 0, 2, Any ) );
        Assert.Equal( 28, sequence.Event( 0, 8, Any ) );
        Assert.Equal( 25, sequence.Event( 0, 14, Any ) );
        Assert.Equal( 1, sequence.Event( 1, 11, Any ) );               // empty spell -> combat default

        // Labels: aliases, roles, and the mode qualification rule.
        Assert.Equal( "Die", sequence.GetActionName( 2 ) );
        Assert.Equal( "-> 2", sequence.GetActionName( 3 ) );
        Assert.Equal( "Walk (combat)", sequence.GetActionName( 0 ) );
        Assert.Equal( "Stand (combat)", sequence.GetActionName( 1 ) );
        Assert.Equal( "Attack", sequence.GetActionName( 4 ) );
        Assert.Equal( "Impact", sequence.GetActionName( 10 ) );
        Assert.Equal( "Block", sequence.GetActionName( 15 ) );
        Assert.Equal( "Alert", sequence.GetActionName( 28 ) );
        Assert.Equal( "Stand (peace), Pillage", sequence.GetActionName( 25 ) );
        Assert.Equal( "Walk (peace)", sequence.GetActionName( 22 ) );
        Assert.Equal( "Run (combat), Run (peace)", sequence.GetActionName( 24 ) );
        Assert.Equal( "Fidget (peace)", sequence.GetActionName( 26 ) );
        Assert.Equal( "Fidget (combat)", sequence.GetActionName( 27 ) );
        Assert.Equal( "Stand (mounted), Fidget (mounted)", sequence.GetActionName( 31 ) );
        Assert.Equal( "Walk (mounted)", sequence.GetActionName( 29 ) );
        Assert.Equal( "Run (mounted)", sequence.GetActionName( 30 ) );
        Assert.Equal( "-> 15", sequence.GetActionName( 16 ) );
        Assert.Equal( "Action 40", sequence.GetActionName( 40 ) );    // no record, no role
    }

    [Fact]
    public void Parse_AliasesLookupsAndEdgeCases()
    {
        var data = BuildSequence(
            1,
            [
                ( 0, 10, -1, 0 ),       // real, lowest real
                ( 7, 10, -1, 2 ),       // real, with counted-list items
                ( 2, 0, 7, 0 ),         // alias -> real 7
                ( 3, 0, 2, 0 ),         // alias -> alias 2 -> fallback 0
                ( 5, 0, 40, 0 ),        // alias -> missing 40 -> fallback 0
                ( 7, 0, 4, 0 ),         // duplicate group 7: first wins
                ( 12, 10, -1, 0 )       // real, no role
            ],
            [
                new TestMode( 0, NoParent, 1,
                    [ ( 0, 1, 1 ) ],
                    [ ( 0, 2, [9, 11] ), ( 0, Any, [4, 5, 6] ), ( 5, Any, [] ) ] ) // empty fidget
            ] );

        var sequence = AnimationSequence.Parse( data )!;

        Assert.Equal( 6, sequence.Actions.Count );                     // duplicate 7 dropped
        Assert.Equal( -1, sequence.Actions.Single( a => a.Group == 7 ).Alias );
        Assert.Equal( 7, sequence.Replaced[2] );                       // one redirect to a real group
        Assert.Equal( 0, sequence.Replaced[3] );                       // alias->alias -> lowest real
        Assert.Equal( 0, sequence.Replaced[5] );                       // alias->missing -> lowest real
        Assert.Equal( 7, sequence.Replaced[7] );
        Assert.Equal( 10, sequence.Replaced[10] );                     // no record -> identity

        // Name shows the raw alias target even when the frames fall back to the lowest real group.
        Assert.Equal( "-> 2", sequence.GetActionName( 3 ) );
        Assert.Equal( "-> 40", sequence.GetActionName( 5 ) );
        Assert.Equal( "-> 7", sequence.GetActionName( 2 ) );
        Assert.Equal( "Action 12", sequence.GetActionName( 12 ) );     // real group, no role
        Assert.Equal( "Action 0", sequence.GetActionName( 0 ) );
        Assert.Equal( "Stand", sequence.GetActionName( 1 ) );           // one mode -> unqualified

        // Sub-action before the generic list; phase indexes the list; empty list falls back to default.
        Assert.Equal( 9, sequence.Event( 0, 0, 2, 0 ) );
        Assert.Equal( 11, sequence.Event( 0, 0, 2, 1 ) );
        Assert.Equal( 4, sequence.Event( 0, 0, 0, 0 ) );
        Assert.Equal( 6, sequence.Event( 0, 0, 0, 2 ) );
        Assert.Equal( 1, sequence.Event( 0, 5, Any ) );                 // empty -> default
        Assert.Equal( 1, sequence.Movement( 0, 0 ) );

        // Short data and truncation.
        Assert.Null( AnimationSequence.Parse( ReadOnlySpan<byte>.Empty ) );
        Assert.Null( AnimationSequence.Parse( new byte[10] ) );
        var cut = AnimationSequence.Parse( data.AsSpan( 0, data.Length - 3 ) )!;
        Assert.NotNull( cut );
        Assert.Equal( 6, cut.Actions.Count );
        Assert.False( cut.HasModes );                                   // truncated mode block -> no modes

        // Cyclic parents terminate (guard), returning the last mode's default.
        var cycle = AnimationSequence.Parse( BuildSequence( 1, [ ( 0, 10, -1, 0 ) ],
        [
            new TestMode( 0, 1, 10, [], [] ),
            new TestMode( 1, 0, 20, [], [] )
        ] ) )!;
        Assert.Equal( 20, cycle.Movement( 0, 0 ) );
    }

    /// <summary>
    ///     Bug 1: the client keys an entry by its name, not the body id stored inside it. Two entries
    ///     with swapped internal ids must still land under their own names.
    /// </summary>
    [Fact]
    public void UopEntries_AreKeyedByName()
    {
        using var dir = new TempDir();
        var named5 = BuildSequence( 7, [ ( 9, 10, -1, 0 ) ], [] );       // entry name says 5, holds id 7
        var named7 = BuildSequence( 5, [ ( 11, 10, -1, 0 ) ], [] );      // entry name says 7, holds id 5

        UOPWriter.Write( dir.File( "AnimationFrame1.uop" ), [] );
        UOPWriter.Write( dir.File( "AnimationSequence.uop" ),
        [
            ( "build/animationsequence/00000005.bin", named5, 0, named5.Length, UOPCompression.None ),
            ( "build/animationsequence/00000007.bin", named7, 0, named7.Length, UOPCompression.None )
        ] );

        using var animations = new Animations( new UOFileSet( dir.Path ) );

        var byName5 = animations.GetSequence( 5 );
        var byName7 = animations.GetSequence( 7 );

        Assert.NotNull( byName5 );
        Assert.NotNull( byName7 );
        Assert.Equal( 7, byName5.InternalId );                            // the entry named 5 holds id 7
        Assert.Equal( 9, byName5.Actions[0].Group );
        Assert.Equal( 5, byName7.InternalId );
        Assert.Equal( 11, byName7.Actions[0].Group );
        Assert.Null( animations.GetSequence( 9 ) );                       // no entry named 9
    }

    /// <summary>Real client: 0x610 is an "animal" whose action 2 is its death, 25 its peace stand, 3 an alias of 2.</summary>
    [Fact]
    public void RottweilerUsesSequenceActionNames()
    {
        var animations = UOTestData.Require().Animations;
        const int Rottweiler = 0x610;

        var info = animations.GetBody( Rottweiler );

        if ( info.Source != AnimationSource.UOP || !info.HasAnimation )
        {
            Assert.Skip( "This client has no UOP body 0x610." );
        }

        var sequence = animations.GetSequence( info.FileBody );
        Assert.NotNull( sequence );
        Assert.True( sequence.HasModes );

        // The table from the brief, resolved through the mode blocks.
        Assert.Equal( 1, sequence.Movement( 0, 0 ) );
        Assert.Equal( 0, sequence.Movement( 0, 1 ) );
        Assert.Equal( 24, sequence.Movement( 0, 2 ) );
        Assert.Equal( 25, sequence.Movement( 1, 0 ) );
        Assert.Equal( 22, sequence.Movement( 1, 1 ) );
        Assert.Equal( 24, sequence.Movement( 1, 2 ) );
        Assert.Equal( 31, sequence.Movement( 2, 0 ) );
        Assert.Equal( 29, sequence.Movement( 2, 1 ) );
        Assert.Equal( 30, sequence.Movement( 2, 2 ) );
        Assert.Equal( 27, sequence.Event( 0, 5, Any ) );
        Assert.Equal( 26, sequence.Event( 1, 5, Any ) );
        Assert.Equal( 31, sequence.Event( 2, 5, Any ) );
        Assert.Equal( 2, sequence.Event( 0, 3, Any ) );
        Assert.Equal( 2, sequence.Event( 1, 3, Any ) );                   // via parent
        Assert.Equal( 2, sequence.Event( 2, 3, Any ) );                   // via parents
        Assert.Equal( 4, sequence.Event( 0, 0, Any ) );
        Assert.Equal( 10, sequence.Event( 0, 4, Any ) );
        Assert.Equal( 15, sequence.Event( 0, 2, Any ) );
        Assert.Equal( 28, sequence.Event( 0, 8, Any ) );
        Assert.Equal( 25, sequence.Event( 0, 14, Any ) );
        Assert.Equal( 1, sequence.Event( 1, 11, Any ) );                  // empty spell -> root default

        Assert.Equal( 2, sequence.Replaced[3] );

        // Names from the sequence, not the fixed per-type tables.
        Assert.Equal( "Die", animations.GetActionName( Rottweiler, 2 ) );
        Assert.Equal( "-> 2", animations.GetActionName( Rottweiler, 3 ) );
        Assert.Contains( "Stand (peace)", animations.GetActionName( Rottweiler, 25 ) );
        Assert.Equal( "Idle", Animations.GetActionName( MobType.Animal, 2 ) );  // the old, wrong name

        // Exactly what get_body_info returns.
        var body = AnimationsTools.DescribeBody( animations, Rottweiler );
        var names = body["actions"]!.AsArray()
            .ToDictionary( a => a!["action"]!.GetValue<int>(), a => a!["name"]!.GetValue<string>() );
        Assert.Equal( "Die", names[2] );
        Assert.Equal( "-> 2", names[3] );
        Assert.Contains( "Stand (peace)", names[25]! );
    }
}
