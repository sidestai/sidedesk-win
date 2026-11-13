# SideDesk

A Windows desktop sidebar utility built with WPF (.NET 10). It provides a persistent AppBar sidebar with sticky notes, todos, data dashboards, AI assistant, cloud drive integration, and a dual-mode plugin system (native DLL + HTML/WebView2).

To get Mac version please visit: https://github.com/sidestai/sidedesk-mac

## Features

- **Persistent Sidebar** — Windows AppBar that stays on screen edge, supports multi-monitor and per-monitor DPI
- **Sticky Notes & Todos** — Markdown-rendered notes with cloud sync
- **Data Dashboard** — Configurable widgets with variable-driven data sources
- **AI Assistant** — Multi-model chat with configurable LLM providers
- **Cloud Drive** — WebDAV / S3 / Baidu Pan integration
- **Plugin System** — Native (.NET DLL) and HTML (WebView2 sandboxed) plugin modes
- **Team Sharing** — Share modules across team members with permission control
- **Multi-language** — 13 languages (zh-CN, zh-TW, en, ja, ko, de, es, fr, it, pl, pt-BR, ru, tr)
- **Auto Update** — Velopack-based self-updating

## Requirements

- Windows 10 19041+ or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- WebView2 Runtime (pre-installed on Windows 11; auto-downloaded on Windows 10)

## Build

```powershell
# Default build (Chinese region backend)
dotnet build SideDesk.slnx -c Release

# Build with international region backend
dotnet build SideDesk.slnx -c Release -p:ApiRegion=en
```

The `ApiRegion` property controls which backend API domain is used at compile time (see `Constants.cs`).

## Project Structure

```
├── SideDesk.slnx           Solution file
├── SideDesk.csproj         Main project
├── Controls/               UI controls (sidebar panels, native mode, etc.)
├── Framework/              Core infrastructure (i18n, settings, runtime log)
├── Models/                 Data models
├── Services/               Business logic services
│   └── Plugins/            Plugin loading and hosting
├── Windows/                Dialog windows and popups
├── Themes/                 Light/Dark theme resources
├── langs/                  Runtime translation dictionaries (JSON)
├── resources/themes/       User-selectable color themes
├── plugins/                Example plugins
│   ├── DeskClock/          Native plugin example
│   ├── HelloWorld/         Native plugin example
│   ├── HelloWorldHtml/     HTML plugin example
│   └── SystemCleaner/      Native plugin with UI
└── docs/
    └── html-plugin-spec.md HTML plugin development specification
```

## Plugin Development

SideDesk supports two plugin modes:

| Mode | Format | Runtime |
|------|--------|---------|  
| Native | .NET DLL implementing `ISideDeskPlugin` | In-process (AssemblyLoadContext) |
| HTML | Static HTML/CSS/JS with `plugin.json` manifest | WebView2 sandbox (isolated origin) |

See [docs/html-plugin-spec.md](docs/html-plugin-spec.md) for the full HTML plugin specification.

Example plugins are in the `plugins/` directory.

## Configuration

Backend API endpoints are defined in `Constants.cs` and selected at build time via the `ApiRegion` property. To use your own backend:

1. Modify the URLs in `Constants.cs`
2. Build with your preferred region flag

## License

See [LICENSE](LICENSE).
