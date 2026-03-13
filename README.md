# Callsmith

**A native, cross-platform API client for developers who want more.**

Callsmith is a desktop HTTP API testing tool built with C# and .NET. Think Postman — but without the bloat, without Electron, and with every feature you've always wished existed.

---

## Vision

Most API tools are either too simple or too heavy. Callsmith aims to be:

- **Fast** — native .NET desktop app, no web runtime overhead
- **Keyboard-friendly** — power-user focused, everything reachable from the keyboard
- **Scriptable** — programmable workflows, request chaining, and environment management
- **Portable** — single-file executables for Windows, macOS, and Linux
- **Offline-first** — no accounts, no cloud sync, no API keys required, everything stays local

---

## Tech Stack

| Layer | Technology |
|---|---|
| Language | C# 13 / .NET 10 (LTS) |
| UI Framework | Avalonia UI (cross-platform native UI) |
| MVVM Toolkit | CommunityToolkit.Mvvm |
| HTTP Client | System.Net.Http.HttpClient |
| Serialization | System.Text.Json |
| Storage | SQLite via EF Core (local persistence) |
| Testing | xUnit + FluentAssertions + NSubstitute |
| Packaging | dotnet publish (self-contained, single-file) |

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- No accounts, API keys, or cloud services required

### Run Locally

```bash
git clone https://github.com/yourname/callsmith
cd callsmith
dotnet restore
dotnet run --project src/Callsmith.Desktop
```

### Build a Distributable

```bash
# Windows (x64) — self-contained single executable
dotnet publish src/Callsmith.Desktop -r win-x64 -c Release --self-contained

# macOS (Apple Silicon)
dotnet publish src/Callsmith.Desktop -r osx-arm64 -c Release --self-contained

# Linux (x64)
dotnet publish src/Callsmith.Desktop -r linux-x64 -c Release --self-contained
```

---

## Project Structure

```
callsmith/
├── src/
│   ├── Callsmith.Desktop/        # Avalonia UI app (entry point, views, view models)
│   ├── Callsmith.Core/           # Business logic, HTTP engine, domain models
│   └── Callsmith.Data/           # EF Core + SQLite persistence layer
├── tests/
│   ├── Callsmith.Core.Tests/     # Unit tests for Core
│   └── Callsmith.Desktop.Tests/  # UI/ViewModel tests
├── docs/                         # Design notes, wireframes, ADRs
├── AGENTS.md                     # AI agent development instructions ← READ THIS FIRST
├── ARCHITECTURE.md               # System design and layer rules
├── CONVENTIONS.md                # Coding conventions and style rules
└── TODO.md                       # Prioritized feature backlog
```

---

## Planned Features

- [ ] HTTP requests — GET, POST, PUT, PATCH, DELETE, OPTIONS, HEAD
- [ ] Environment variable management with secret masking
- [ ] Request collections and folders
- [ ] Response diff viewer
- [ ] Full request history with search
- [ ] Cookie jar management
- [ ] Auth helpers — Bearer, Basic, OAuth2 PKCE
- [ ] Request chaining and pre/post scripts
- [ ] OpenAPI / Swagger import
- [ ] Response body formatting — JSON, XML, HTML
- [ ] WebSocket support
- [ ] gRPC support

---

## Contributing

See [AGENTS.md](./AGENTS.md) for how AI agents should work in this codebase.
See [CONVENTIONS.md](./CONVENTIONS.md) for coding standards.
See [ARCHITECTURE.md](./ARCHITECTURE.md) for system design decisions.
