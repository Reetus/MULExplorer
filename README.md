# MUL Explorer

A read-only explorer for Ultima Online client files (MUL and UOP), modelled on
[UOFiddler](https://github.com/polserver/UOFiddler). Built with Avalonia and NativeAOT for
Windows and Linux, with a built-in [MCP](https://modelcontextprotocol.io) server so AI agents can
query the data and drive the UI.

Tabs, in UOFiddler's order: Multis, Animations, Items, Land Tiles, Textures, Gumps, Sounds, Hues,
Fonts, CliLoc, Map, Light, Speech, Skills, AnimData, MultiMap/Facets, Dress, TileData, RadarColor,
SkillGrp, Verdata.

## Install

Download from [Releases](https://github.com/Reetus/MULExplorer/releases):

- **Windows:** `MULExplorer-win-x64-Setup.exe` (per-user install, no admin) or the portable zip.
- **Linux:** `MULExplorer-linux-x64.AppImage` (`chmod +x` and run).

Installed copies update themselves from GitHub Releases. On first run choose your client folder
with **File › Set UO Folder…** (the folder containing `tiledata.mul`).

## MCP

The app serves MCP over Streamable HTTP at `http://127.0.0.1:5006/mcp` while it runs (MCP menu to
start/stop/copy the URL). It can also run headless:

```
MULExplorer --mcp-stdio [--uo-folder PATH]
MULExplorer --mcp-http [--mcp-port 5006] [--uo-folder PATH]
```

See [mcp.example.json](mcp.example.json) for client configuration. Data tools work headless;
`ui_*` tools (select tabs/entries, read tab state, screenshots) need the window.

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet test MULExplorer.Tests          # data tests need UO_DATA_FOLDER=<client folder>, otherwise they skip
dotnet run --project MULExplorer -- --uo-folder <client folder>
```

Packages: `branding/windows/build-setup.ps1` (Setup.exe) and `branding/linux/build-appimage.sh`
(AppImage), both via [Velopack](https://velopack.io). Releases are built by the **Release**
workflow (Actions › Release › Run workflow, enter a version).

See [AGENTS.md](AGENTS.md) for architecture and conventions.
