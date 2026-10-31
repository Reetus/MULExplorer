# AGENTS.md — MUL Explorer

Read-only Avalonia 12 viewer for Ultima Online client files (MUL/UOP), modelled on UOFiddler
(`uofiddler-src/`, reference only, git-ignored). Tabs follow UOFiddler's order minus its Start tab.
Every tab is drivable through MCP and headless tests. NativeAOT on Windows and Linux.

## Commands

```bash
dotnet build                                   # whole solution (MULExplorer.slnx); warnings are errors
dotnet test MULExplorer.Tests                  # needs a client for data tests, see below
dotnet test MULExplorer.Tests --filter "FullyQualifiedName~DataTests"
scripts/test-wsl.sh                            # same suite on Linux (run inside WSL; mirrors repo to ~/mulexplorer-wsl)
dotnet publish MULExplorer -c Release -r win-x64   -o artifacts/win-x64     # NativeAOT
dotnet publish MULExplorer -c Release -r linux-x64 -o artifacts/linux-x64   # NativeAOT (run on Linux/WSL)
```

Data-driven tests need a client (`UO_DATA_FOLDER=none` forces them to skip, as on CI): set `UO_DATA_FOLDER` (e.g. `D:\Games\OSI\UO`, or `/mnt/d/Games/OSI/UO`
in WSL), otherwise they fall back to the folder saved in the user's MULExplorer settings, otherwise skip.
Tests redirect the app's settings file to a temp path (`MULEXPLORER_SETTINGS`), never the user's.

Run the app headless as an MCP server:

```bash
MULExplorer --mcp-stdio [--uo-folder PATH]
MULExplorer --mcp-http [--mcp-port 5006] [--uo-folder PATH]   # http://127.0.0.1:5006/mcp
```

The GUI also auto-starts the HTTP server (settings `mcpPort`/`mcpAutoStart`), adding `ui_*` tools.

## Projects

| Project | Purpose |
|---|---|
| `UODataAccess` | File formats + loaders. **No UI deps.** AOT-compatible (`IsAotCompatible`), unsafe allowed. |
| `MULExplorer.Mcp` | MCP server on the official C# SDK (`ModelContextProtocol` 2.2): stdio + Streamable HTTP (Kestrel, loopback). |
| `MULExplorer` | Avalonia 12 app (Fluent theme, dark by default), CommunityToolkit.Mvvm, `PublishAot`. Entry point `Program.cs`. |
| `MULExplorer.Tests` | xUnit v3 + Avalonia.Headless.XUnit (real `App`, Skia rendering, so screenshots work). |

## Data layer (`UODataAccess`)

- `UOData` is the entry point: one per client folder, every loader is a lazily-created, thread-safe property
  (`public Foo Foo => Get( ref field, static f => new Foo( f ) );`). Missing files throw
  `FileNotFoundException` on first access; `UOData.TryGet( d => d.Foo )` probes.
- `IO/UOFileSet` — case-insensitive file lookup (Linux!). Never `Path.Combine` a client file name yourself.
- `IO/MappedFile` — memory-mapped (or in-memory for small files) read-only file exposing `ReadOnlySpan<byte>`.
- `IO/FileIndex` — id → payload for **either** `xxx.mul`+`xxxidx.mul` **or** `xxxLegacyMUL.uop`
  (`FileIndex.Open( files, idx, mul, uop, "build/xxx/{0:D8}.ext", length, uopHasExtra )`). UOP decompression
  (zlib, zlib+BWT) is handled; `TryRead( id, out ReadOnlySpan<byte> data, out IndexEntry entry )`.
  `IndexEntry.Extra` is the idx extra field (`ExtraHigh`/`ExtraLow` halves).
- `IO/UOPArchive` — raw UOP container access for formats that are not id-indexed (multis, animations, tileart).
- `Imaging/Pixmap` — decoded image, `uint` pixels `0xAARRGGBB` (= Avalonia `Bgra8888`). `Color16` converts
  UO ARGB1555. `PngEncoder` writes PNGs (used by MCP image results).
- Implemented loaders: `TileData`, `Hues`, `Art`, `Gumps`, `Cliloc`, `RadarColors`. Others are stubs marked `TODO`.

Style for format code: `BinaryPrimitives` over spans, `stackalloc`/`ArrayPool` instead of temp arrays,
`MemoryMarshal.Cast` for fixed-layout records, no `BinaryReader` over `MemoryStream` in hot paths.
References for formats, in priority order: `D:\Code\ClassicUO2\src\ClassicUO.Assets\*Loader.cs` and
`ClassicUO.IO` (**primary** for data access: modern client, UOP formats), then `uofiddler-src/Ultima/*.cs`
(only to match what UOFiddler shows, or where ClassicUO2 lacks a format), then
`D:\Code\HouseDesigner\HouseDesigner.AssetLoader` (multis incl. `MultiCollection.uop`, housing.bin),
`D:\Code\CACL\CACL\UO\Data` (map/statics/texmaps/radarcol/speech/skills).

## App (`MULExplorer`)

- **Tabs**: `ViewModels/Tabs/<Name>ViewModel.cs` + `Views/Tabs/<Name>View.axaml`. VM → View mapping is the
  compiled `DataTemplate` list in `Views/MainWindow.axaml` (no reflection view locator). Order is set in
  `MainWindowViewModel`. Placeholder tabs derive `PlaceholderTabViewModel`; real ones derive `TabViewModel`.
- `TabViewModel` contract: `LoadAsync( UOData )` runs once per folder (heavy work in `Task.Run`, then assign
  bound properties); `Reset()` drops folder-derived state; automation hooks `GetState()` (extend
  `base.GetState()`), `SelectEntry( id )`, `Search( text )`, `Actions` + `Invoke( action, args )`.
  These power the `ui_*` MCP tools and the headless tests — implement them for every tab.
- **`ItemsViewModel` / `ItemsView` is the reference implementation**; copy its shape.
- `Controls/TileGrid` — virtualized thumbnail grid (ids + `Func<int, Bitmap?>`), use it for any art grid.
  `Services/BitmapCache` (LRU) + `Pixmap.ToBitmap()` for images.
- **Strings**: all user-visible text comes from `.resx` — `Resources/Strings.resx` (shell/common) and one
  `Resources/<Tab>Strings.resx` per tab. The build generates a class per file (`MULExplorer.Resources.<File>`).
  XAML: `Text="{x:Static r:ItemsStrings.Weight}"` with `xmlns:r="using:MULExplorer.Resources"`.
  `scripts/new-resx.sh FILE < key=value lines` writes a resx. MCP tool descriptions stay inline (English).
- No `Avalonia.Controls.DataGrid`: it is not trim/AOT safe (reflection in its binding helpers). For tables use a
  `ListBox` with a compiled `DataTemplate` (Grid columns + a header row) or `ItemsRepeater`.
- Theme: Fluent, `RequestedThemeVariant` Dark/Light/Default; use `DynamicResource` theme brushes, never
  hard-coded colors, so both themes work. Shared styles in `Styles.axaml` (`label`, `status`, `placeholder`,
  `preview`, `chip` classes).
- Settings: `Services/Settings.cs`, source-generated JSON in `%LocalAppData%/MULExplorer/settings.json`.
  Profiles are out of scope.

## MCP (`MULExplorer.Mcp`)

- Official SDK, low-level `ListToolsHandler`/`CallToolHandler` over our `ToolRegistry` — no `[McpServerTool]`
  attribute reflection (AOT). One `IToolProvider` per tab in `Tools/<Tab>Tools.cs`, listed in
  `MULExplorerMcpServer.Providers`.
- `registry.Add( name, description, Props( ("id", Id("...")), ... ), ["id"], args => ... )`; args via
  `ToolArgs` (`RequireInt` accepts `0x` hex); results via `Json(...)`, `Text(...)`, `Image( pixmap, info )`,
  `Error(...)`. Tool names are `snake_case`. Data tools are read-only and work headless; `UITools` drive the GUI
  through `IUIAutomation` (implemented by `Services/UIAutomation.cs`).

## Conventions

- Rider style from `.editorconfig`: spaces inside parens (`Foo( a, b )`, `if ( x )`), `var`, braces always,
  file-scoped namespaces. Acronyms upper-case: `UO`, `UOP`, `MUL`, `BWT`, `ID`, `IO`, `UI`, `JSON`.
- `TreatWarningsAsErrors` everywhere. AOT: no reflection, source-generated JSON, compiled bindings, generic
  `Enum.GetValues<T>()`.
- Read-only: nothing ever writes to the client folder.
- Data files (name lists etc.) are `.json`, never UOFiddler's `.xml`: embedded resources under `Data/`, read
  with a source-generated `JsonSerializerContext`. Convert UOFiddler XML once; commit only the JSON.
- Tests: format tests on synthetic files where possible (see `IOTests`, `UOPWriter`), data tests via
  `UOTestData.Require()`, UI tests with `[AvaloniaFact]` via `App.CreateMainWindow( UOTestData.IsolatedSettings(...) )`
  and the `IUIAutomation` API. Pass `TestContext.Current.CancellationToken` where accepted.
