# Callsmith

Postman made API testing mainstream. It also normalized slow startup, cloud push, and Electron memory overhead.

Callsmith is the alternative for developers who want raw speed, local-first control, and architecture they can trust.

If you are using Postman, Bruno, or Insomnia today, this is built to make switching feel obvious.

Most API clients now ship mandatory platform changes that can break local collections, lock cloud workflows, or degrade UI behavior without warning. Callsmith does not auto-update. What you download is exactly what you run until you choose to install a newer version.

## Why Switch

### Postman users

Postman is powerful, but many teams pay for that power with UI lag, account-first workflows, and cloud-centric defaults.

Callsmith gives you:

- Native desktop performance on .NET 10 (not a browser runtime app pretending to be desktop)
- Local-first operation: no account, no API key, no mandatory cloud sync
- Filesystem-native collections that work with git workflows naturally
- Full request/response visibility in one screen without tab-juggling

### Bruno users

Bruno got one major thing right: local files.

Callsmith keeps that filesystem-native philosophy and pushes deeper into a strongly typed architecture:

- C# domain model and service abstractions in a dedicated Core layer
- Clean separation: Desktop UI, Core logic, Data persistence
- More polished split-pane workflow UI focused on high-speed request/response iteration
- More advanced environment variable handling: multi-environment switching, secret masking, and runtime substitution across URL, headers, and body
- Test-first core services (xUnit + FluentAssertions + NSubstitute)
- Native HttpClient transport layer with protocol registry design for future expansion

### Insomnia users

Insomnia built an early reputation as a strong developer-first tool, but many teams feel quality and core feature depth slipped as the product became more corporatized.

Callsmith is the reset: local-first, performance-focused, and engineered around developer workflows instead of platform lock-in.

Callsmith currently includes:

- Working Insomnia v5 collection import
- Environment + sub-environment import support
- Variable syntax normalization to Callsmith format
- A migration path to local, versionable collection files

## What You Get Today

These are implemented now, not wishlist items:

- HTTP methods: GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS
- Native request editor with method, URL, headers, query params, path params, body, and auth
- Body modes: none, JSON, plain text, XML, form-urlencoded, multipart
- Auth modes: none, Bearer, Basic, API key (header/query)
- Full response panel with status, elapsed time, size, headers, and body
- Filesystem-native collections and folder tree
- Open folder as collection, recent collections, rename/create/delete requests and folders
- Disk change watching (external edits reflected without restart)
- Unsaved-change guard + keyboard-first save flow
- Environment management with static variables and secret masking
- Variable substitution in URL, headers, and body
- Insomnia import support

## Current Keyboard Shortcuts

These shortcuts are implemented now in the current desktop app.

### Global

| Shortcut | Action |
|---|---|
| Ctrl+S | Save current context (environment editor when open, otherwise active request tab) |
| Ctrl+P | Open command palette |
| Ctrl+Enter | Send request |
| Alt+R | Reveal active request in collections |

### Collections Tree

| Shortcut | Action |
|---|---|
| F2 | Rename selected request/folder |
| Delete | Delete selected request/folder |
| Enter | Confirm rename/create dialog |
| Escape | Cancel rename/create dialog |

### Command Palette

| Shortcut | Action |
|---|---|
| Up / Down | Move selection |
| Enter | Execute selected command |
| Escape | Close palette |

### Environment Variable Completion Popup

| Shortcut | Action |
|---|---|
| Up / Down | Move suggestion selection |
| Enter / Tab | Insert selected variable |
| Escape | Close suggestion popup |

## The Technical Bet (And Why It Matters)

Callsmith is intentionally opinionated about engineering quality.

| Layer | Choice | Why this is better for developers |
|---|---|---|
| Runtime | C# 13 / .NET 10 (LTS) | Native performance profile, mature tooling, predictable memory behavior |
| Desktop UI | Avalonia UI | One native codebase for Windows, macOS, Linux |
| HTTP Engine | System.Net.Http.HttpClient | Battle-tested .NET networking stack, no wrapper magic |
| Architecture | Core + Desktop + Data layering | Business logic remains testable and UI-agnostic |
| Persistence | SQLite via EF Core + filesystem collections | Local control, git-friendly request files, structured data where needed |
| Serialization | System.Text.Json | Fast built-in serializer with zero external lock-in |
| Testing | xUnit + FluentAssertions + NSubstitute | Reliable unit testing around core behaviors |

This stack is not trendy for the sake of trend. It is chosen to keep the app fast, deterministic, and maintainable as features grow.

## Head-to-Head Snapshot

| Concern | Callsmith | Typical pain in legacy API clients |
|---|---|---|
| Startup and responsiveness | Native .NET desktop app | Electron/web-runtime overhead |
| UI workflow polish | Full request+response visibility in one coordinated view | More tab/context switching and less cohesive editing flow |
| Update control | No auto-update; you choose when to install a newer version | Mandatory or surprise platform changes that can disrupt workflows |
| Data ownership | Local-first by default | Account/cloud-first pressure |
| Environment variables | Secrets-aware variables with runtime substitution and environment swapping | Simpler variable models with less end-to-end request resolution support |
| Team workflow | Filesystem-native collections, git-friendly | Proprietary workspace friction |
| Architecture transparency | Clear layered codebase in C# | Mixed UI/business logic and harder local extension paths |
| Offline use | Fully usable offline | Features increasingly tied to online services |

## Roadmap Highlights

Planned next:

- Deep searchable request history with advanced filters
- Response viewer upgrades (tree/raw toggles, format improvements, diffs)
- OpenAPI import
- WebSocket and gRPC transports
- Dynamic script/chained variables

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
# Windows (x64) - self-contained single executable
dotnet publish src/Callsmith.Desktop -r win-x64 -c Release --self-contained

# macOS (Apple Silicon)
dotnet publish src/Callsmith.Desktop -r osx-arm64 -c Release --self-contained

# Linux (x64)
dotnet publish src/Callsmith.Desktop -r linux-x64 -c Release --self-contained
```

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
├── docs/                         # Design notes, ADRs
├── AGENTS.md                     # AI agent development instructions
├── ARCHITECTURE.md               # System design and layer rules
├── CONVENTIONS.md                # Coding conventions and style rules
└── TODO.md                       # Prioritized feature backlog
```

## Contributing

See [AGENTS.md](./AGENTS.md) for how AI agents should work in this codebase.
See [CONVENTIONS.md](./CONVENTIONS.md) for coding standards.
See [ARCHITECTURE.md](./ARCHITECTURE.md) for system design decisions.
