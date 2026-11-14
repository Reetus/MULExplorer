using System.Security.Cryptography;
using System.Runtime.InteropServices;
using UODataAccess.Imaging;

namespace UODataAccess.Merge;

/// <summary>
///     What a merge did with one entry. "Local" is the customised client being updated, "upstream" the
///     newer official client, "stock" any official client (upstream included) used to tell stale
///     official content apart from custom work.
/// </summary>
public enum MergeAction
{
    /// <summary>Local and upstream agree.</summary>
    Unchanged,

    /// <summary>Local had an older official version; upstream's replaces it.</summary>
    Updated,

    /// <summary>Only upstream has it.</summary>
    Added,

    /// <summary>Local differs from every stock version: custom work, kept.</summary>
    KeptCustom,

    /// <summary>Only local has it (custom addition, or something upstream dropped), kept.</summary>
    KeptLocalOnly
}

/// <param name="Key">Display key: entry id, or hash for unnamed UOP entries.</param>
/// <param name="Action">What happened.</param>
/// <param name="LocalIsStock">For <see cref="MergeAction.KeptLocalOnly" />: the content matches some stock client.</param>
/// <param name="Id">Numeric id within the asset (art: raw index incl. land), or -1.</param>
public readonly record struct MergeItem( string Key, MergeAction Action, bool LocalIsStock = false, int Id = -1 );

/// <summary>Outcome of merging one asset (one output file).</summary>
public sealed class AssetMergeResult( string asset )
{
    public string Asset { get; } = asset;

    /// <summary>Written file, or null for a dry run or when nothing needed writing.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Upstream entries re-stored in local's compression instead of copied verbatim.</summary>
    public int Transcoded { get; set; }

    public Dictionary<MergeAction, int> Counts { get; } = Enum.GetValues<MergeAction>().ToDictionary( a => a, _ => 0 );

    /// <summary>Every entry that is not <see cref="MergeAction.Unchanged" />.</summary>
    public List<MergeItem> Items { get; } = [];

    /// <summary>True when the merged result is byte-for-byte what upstream ships (nothing custom survived).</summary>
    public bool IsPureUpstream => Counts[MergeAction.KeptCustom] == 0 && Counts[MergeAction.KeptLocalOnly] == 0;

    public void Record( string key, MergeAction action, bool localIsStock = false, int id = -1 )
    {
        Counts[action]++;

        if ( action != MergeAction.Unchanged )
        {
            Items.Add( new MergeItem( key, action, localIsStock, id ) );
        }
    }

    public override string ToString() =>
        $"{Asset}: {Counts[MergeAction.Unchanged]} unchanged, {Counts[MergeAction.Updated]} updated, {Counts[MergeAction.Added]} added, " +
        $"{Counts[MergeAction.KeptCustom]} custom kept, {Counts[MergeAction.KeptLocalOnly]} local-only kept" +
        ( Transcoded > 0 ? $", {Transcoded} re-stored in local's compression" : "" );
}

/// <summary>The per-entry rule shared by every asset merger.</summary>
public static class MergeRules
{
    /// <summary>
    ///     Decides one entry. <paramref name="localIsStock" /> is only evaluated when local and upstream
    ///     differ (it is the expensive part: decoding older clients).
    /// </summary>
    public static MergeAction Decide( bool hasLocal, bool hasUpstream, bool same, Func<bool> localIsStock )
    {
        if ( !hasLocal )
        {
            return MergeAction.Added;
        }

        if ( !hasUpstream )
        {
            return MergeAction.KeptLocalOnly;
        }

        if ( same )
        {
            return MergeAction.Unchanged;
        }

        return localIsStock() ? MergeAction.Updated : MergeAction.KeptCustom;
    }

    /// <summary>True when the local copy wins.</summary>
    public static bool TakesLocal( this MergeAction action ) => action is MergeAction.KeptCustom or MergeAction.KeptLocalOnly;
}

/// <summary>128-bit content fingerprints (truncated SHA-256) for comparing entries across clients.</summary>
public static class Fingerprint
{
    public static UInt128 Of( ReadOnlySpan<byte> data )
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData( data, hash );
        return MemoryMarshal.Read<UInt128>( hash );
    }

    /// <summary>Hashes dimensions and pixels, so differently encoded copies of one image match.</summary>
    public static UInt128 Of( Pixmap pixmap )
    {
        using var sha = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
        Span<byte> dims = stackalloc byte[8];
        MemoryMarshal.Write( dims, pixmap.Width );
        MemoryMarshal.Write( dims[4..], pixmap.Height );
        sha.AppendData( dims );
        sha.AppendData( MemoryMarshal.AsBytes( pixmap.Pixels.AsSpan() ) );
        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset( hash );
        return MemoryMarshal.Read<UInt128>( hash );
    }
}
