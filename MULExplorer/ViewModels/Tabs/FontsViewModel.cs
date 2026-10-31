using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Mcp;
using MULExplorer.Mcp.Tools;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>An entry of the font list (UOFiddler's ASCII / Unicode tree).</summary>
public sealed record FontEntry( bool IsUnicode, int Number, string Label );

/// <summary>
///     Fonts tab, after UOFiddler's FontsControl: pick an ASCII (fonts.mul) or Unicode
///     (unifont*.mul) font, browse its characters in a grid, see a character's metrics, and preview
///     typed text rendered with the font (and a hue).
/// </summary>
public sealed partial class FontsViewModel : TabViewModel
{
    /// <summary>Glyphs and text are drawn on black, like in game, so white Unicode glyphs read in both themes.</summary>
    private const uint Backdrop = 0xFF000000;

    private IReadOnlyList<int> _allChars = [];
    private BitmapCache? _glyphs;

    public FontsViewModel() : base( "Fonts", Strings.TabFonts )
    {
    }

    [ObservableProperty]
    public partial bool IsUnicode { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<FontEntry> FontList { get; private set; } = [];

    [ObservableProperty]
    public partial FontEntry? SelectedFont { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<int> VisibleChars { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedChar { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewText { get; set; } = FontsStrings.PreviewTextDefault;

    [ObservableProperty]
    public partial int PreviewHue { get; set; }

    [ObservableProperty]
    public partial Bitmap? GlyphPreview { get; private set; }

    [ObservableProperty]
    public partial Bitmap? TextPreview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string CharStatus { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public Func<int, Bitmap?> GlyphProvider => code => _glyphs?.Get( code );

    public override IReadOnlyList<string> Actions => ["set_font", "set_preview_text", "set_preview_hue"];

    private Fonts? FontData => Data?.TryGet( d => d.Fonts );

    protected override async Task LoadAsync( UOData data )
    {
        var fonts = await Task.Run( () => data.Fonts );
        IsUnicode = fonts.Ascii.Count == 0;
        BuildFontList( fonts );
        await SelectFontAsync( FontList.FirstOrDefault() );
    }

    protected override void Reset()
    {
        _glyphs?.Clear();
        _glyphs = null;
        _allChars = [];
        FontList = [];
        SelectedFont = null;
        VisibleChars = [];
        GlyphPreview = null;
        TextPreview = null;
        Details.Clear();
        Summary = "";
        CharStatus = "";
    }

    private void BuildFontList( Fonts fonts )
    {
        FontList = IsUnicode
            ? fonts.UnicodeFontIds.Select( i => new FontEntry( true, i, string.Format( CultureInfo.CurrentCulture, FontsStrings.UnicodeFontItem, i, Fonts.UnicodeFileName( i ) ) ) ).ToList()
            : fonts.Ascii.Select( f => new FontEntry( false, f.Index, string.Format( CultureInfo.CurrentCulture, FontsStrings.FontItem, f.Index ) ) ).ToList();
    }

    partial void OnIsUnicodeChanged( bool value )
    {
        if ( FontData is { } fonts && IsLoaded )
        {
            BuildFontList( fonts );
            SelectedFont = FontList.FirstOrDefault();
        }
    }

    partial void OnSelectedFontChanged( FontEntry? value )
    {
        if ( IsLoaded )
        {
            _ = SelectFontAsync( value );
        }
    }

    partial void OnSelectedCharChanged( int value ) => UpdateCharacter();

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnPreviewTextChanged( string value ) => UpdateTextPreview();

    partial void OnPreviewHueChanged( int value ) => UpdateTextPreview();

    /// <summary>Switches font: enumerating a Unicode font's 65536 slots happens off the UI thread.</summary>
    private async Task SelectFontAsync( FontEntry? entry )
    {
        if ( FontData is not { } fonts || entry is null )
        {
            _allChars = [];
            ApplyFilter();
            return;
        }

        var chars = await Task.Run( () => CharactersOf( fonts, entry ) );

        if ( !ReferenceEquals( entry, SelectedFont ) && SelectedFont is not null )
        {
            return;
        }

        SetFont( fonts, entry, chars );
    }

    private static IReadOnlyList<int> CharactersOf( Fonts fonts, FontEntry entry ) =>
        entry.IsUnicode
            ? fonts.GetUnicode( entry.Number )?.Characters ?? []
            : Enumerable.Range( AsciiFont.FirstChar, AsciiFont.GlyphCount ).ToList();

    private void SetFont( Fonts fonts, FontEntry entry, IReadOnlyList<int> chars )
    {
        if ( !ReferenceEquals( SelectedFont, entry ) )
        {
            SelectedFont = entry;
        }

        _glyphs?.Clear();
        _glyphs = new BitmapCache( code => GlyphImage( fonts, entry, code, 1 ).ToBitmap() );
        OnPropertyChanged( nameof( GlyphProvider ) );
        _allChars = chars;
        Summary = entry.IsUnicode
            ? string.Format( CultureInfo.CurrentCulture, FontsStrings.FontSummary, chars.Count )
            : $"{string.Format( CultureInfo.CurrentCulture, FontsStrings.FontSummary, chars.Count )}    {FontsStrings.LineHeight}: {fonts.GetAscii( entry.Number )?.Height}";
        ApplyFilter();

        if ( !VisibleChars.Contains( SelectedChar ) )
        {
            SelectedChar = VisibleChars.Contains( 'A' ) ? 'A' : VisibleChars.FirstOrDefault( -1 );
        }

        UpdateCharacter();
        UpdateTextPreview();
    }

    /// <summary>A glyph on a black backdrop with a 1 px margin, scaled by an integer factor.</summary>
    private static Pixmap? GlyphImage( Fonts fonts, FontEntry entry, int code, int scale )
    {
        var glyph = entry.IsUnicode
            ? fonts.GetUnicode( entry.Number )?.GetGlyph( code )?.ToPixmap()
            : fonts.GetAscii( entry.Number ) is { } f && code is >= AsciiFont.FirstChar and <= 0xFF ? f.Glyphs[code - AsciiFont.FirstChar].ToPixmap() : null;

        if ( glyph is null )
        {
            return null;
        }

        var result = new Pixmap( ( glyph.Width + 2 ) * scale, ( glyph.Height + 2 ) * scale );
        Array.Fill( result.Pixels, Backdrop );

        for ( var y = 0; y < glyph.Height; y++ )
        {
            for ( var x = 0; x < glyph.Width; x++ )
            {
                var p = glyph[x, y];

                if ( p >> 24 == 0 )
                {
                    continue;
                }

                for ( var sy = 0; sy < scale; sy++ )
                {
                    result.Row( ( y + 1 ) * scale + sy ).Slice( ( x + 1 ) * scale, scale ).Fill( p );
                }
            }
        }

        return result;
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();

        if ( text.Length == 0 )
        {
            VisibleChars = _allChars;
            return;
        }

        int? code = null;

        try
        {
            code = FontsTools.ParseChar( text );
        }
        catch ( ArgumentException )
        {
        }

        // A code (A, 0x41, U+0041, 65) finds that character; any other text finds each of its characters.
        VisibleChars = code is { } single ? _allChars.Where( c => c == single ).ToList() : _allChars.Where( c => text.Contains( ( char )c ) ).ToList();
    }

    private void UpdateCharacter()
    {
        Details.Clear();

        if ( FontData is not { } fonts || SelectedFont is not { } entry || SelectedChar < 0 )
        {
            GlyphPreview = null;
            CharStatus = "";
            return;
        }

        var code = SelectedChar;
        GlyphPreview = GlyphImage( fonts, entry, code, 6 ).ToBitmap();
        var charText = code is >= 0x20 and < 0xD800 or > 0xDFFF ? ( ( char )code ).ToString() : "";
        Details.Add( new PropertyRow( FontsStrings.Character, $"'{charText}'" ) );
        Details.Add( new PropertyRow( FontsStrings.Code, entry.IsUnicode ? $"U+{code:X4} ({code})" : $"0x{code:X2} ({code})" ) );

        if ( entry.IsUnicode )
        {
            var font = fonts.GetUnicode( entry.Number );
            var glyph = font?.GetGlyph( code );
            Details.Add( new PropertyRow( Strings.Width, glyph?.Width.ToString( CultureInfo.CurrentCulture ) ?? "-" ) );
            Details.Add( new PropertyRow( Strings.Height, glyph?.Height.ToString( CultureInfo.CurrentCulture ) ?? "-" ) );
            Details.Add( new PropertyRow( FontsStrings.XOffset, glyph?.XOffset.ToString( CultureInfo.CurrentCulture ) ?? "-" ) );
            Details.Add( new PropertyRow( FontsStrings.YOffset, glyph?.YOffset.ToString( CultureInfo.CurrentCulture ) ?? "-" ) );
            Details.Add( new PropertyRow( FontsStrings.Advance, font?.Advance( ( char )code ).ToString( CultureInfo.CurrentCulture ) ?? "-" ) );
            CharStatus = $"'{charText}' : {code} (0x{code:X})  {FontsStrings.XOffset}: {glyph?.XOffset}  {FontsStrings.YOffset}: {glyph?.YOffset}";
        }
        else if ( fonts.GetAscii( entry.Number ) is { } font && code is >= AsciiFont.FirstChar and <= 0xFF )
        {
            var glyph = font.Glyphs[code - AsciiFont.FirstChar];
            Details.Add( new PropertyRow( Strings.Width, glyph.Width.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( Strings.Height, glyph.Height.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( FontsStrings.YOffset, font.OffsetY( ( char )code ).ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( FontsStrings.Unknown, glyph.Unknown.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( FontsStrings.Header, font.Header.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( FontsStrings.LineHeight, font.Height.ToString( CultureInfo.CurrentCulture ) ) );
            CharStatus = $"'{charText}' : {code} (0x{code:X})";
        }
    }

    private void UpdateTextPreview()
    {
        if ( Data is not { } data || SelectedFont is not { } entry || string.IsNullOrEmpty( PreviewText ) )
        {
            TextPreview = null;
            return;
        }

        var pixmap = FontsTools.Render( data, entry.IsUnicode, entry.Number, PreviewText, PreviewHue );
        TextPreview = pixmap is null ? null : FontsTools.OnBackground( pixmap, Backdrop ).ToBitmap();
    }

    /// <summary>Selects a character by its code (0x20..0xFF for ASCII, 0..0xFFFF for Unicode).</summary>
    public override bool SelectEntry( int id )
    {
        if ( !_allChars.Contains( id ) )
        {
            return false;
        }

        if ( !VisibleChars.Contains( id ) )
        {
            SearchText = "";
        }

        SelectedChar = id;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleChars.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_font":
            {
                var fonts = FontData ?? throw new InvalidOperationException( "No fonts loaded." );
                var unicode = ( args["kind"]?.GetValue<string>() ?? ( IsUnicode ? "unicode" : "ascii" ) ).Equals( "unicode", StringComparison.OrdinalIgnoreCase );
                var number = ToolArgs.ParseInt( args["font"]?.ToString() ) ?? 0;

                if ( unicode != IsUnicode )
                {
                    IsUnicode = unicode;
                }

                var entry = FontList.FirstOrDefault( f => f.Number == number ) ?? throw new ArgumentException( $"No {( unicode ? "Unicode" : "ASCII" )} font {number}." );
                SetFont( fonts, entry, CharactersOf( fonts, entry ) );
                return GetState();
            }
            case "set_preview_text":
                PreviewText = args["text"]?.GetValue<string>() ?? "";
                return GetState();
            case "set_preview_hue":
                PreviewHue = ToolArgs.ParseInt( args["hue"]?.ToString() ) ?? 0;
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["kind"] = IsUnicode ? "unicode" : "ascii";
        state["font"] = SelectedFont?.Number;
        state["fonts"] = new JsonArray( FontList.Select( f => ( JsonNode? )f.Number ).ToArray() );
        state["total"] = _allChars.Count;
        state["visible"] = VisibleChars.Count;
        state["search"] = SearchText;
        state["selectedChar"] = SelectedChar;
        state["selectedWidth"] = Details.FirstOrDefault( d => d.Label == Strings.Width )?.Value;
        state["previewText"] = PreviewText;
        state["previewHue"] = PreviewHue;
        state["textPreviewSize"] = TextPreview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}
