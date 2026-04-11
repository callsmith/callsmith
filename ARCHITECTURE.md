# Architecture — Callsmith

> Read this before making any structural decisions. This document describes the
> system design, layer responsibilities, and data flows.

---

## Guiding Principles

1. **Separation of concerns** — UI, business logic, and data are in separate
   projects with a strict, one-way dependency graph.
2. **Testability first** — every layer containing logic is independently testable
   without a UI or a live network connection.
3. **No external runtime dependencies** — the app ships as a self-contained
   executable. Users install nothing.
4. **Native performance** — Avalonia renders via Skia directly on each platform.
   No web runtimes. The app should feel instant.
5. **Transport-agnostic core** — the request/response pipeline is built around an
   `ITransport` abstraction. HTTP/REST is the first implementation. gRPC,
   WebSocket, and others are added as new implementations without touching the
   core pipeline or the UI.
6. **UI is a guest, not a host** — `Callsmith.Core` is the entire application.
   `Callsmith.Desktop` is one possible front-end for it. The core engine must be
   fully operable without any UI — every capability must be reachable through
   Core's public service interfaces alone. A CLI front-end (`Callsmith.Cli`),
   a test harness, or any other host should be addable without modifying Core.

---

## Dependency Graph

```
Callsmith.Desktop  (or Callsmith.Cli, or any future host)
       │
       ├──► Callsmith.Core ◄── Callsmith.Data
       │
       └──► Callsmith.Data
```

More explicitly:

| Project | May depend on | May NOT depend on |
|---|---|---|
| `Callsmith.Core` | .NET BCL only | Desktop, Data, Avalonia, EF Core |
| `Callsmith.Data` | Core, EF Core, SQLite | Desktop, Avalonia |
| `Callsmith.Desktop` | Core, Data (via DI only) | Nothing in Data directly — use Core interfaces |

The golden rule: **Core knows nothing about how data is stored or how the UI works.**

**The UI decoupling rule:** every feature of the application must be fully
reachable through `Callsmith.Core`'s public service interfaces without
instantiating anything from `Callsmith.Desktop`. The Desktop project is a
front-end — it renders state and captures input. It does not own any logic.
A future `Callsmith.Cli` project should be addable by referencing only `Core`
and `Data`, with zero changes to either.

---

## Project Descriptions

### Callsmith.Core

The heart of the application. Pure C#, no framework dependencies.

```
Callsmith.Core/
├── Models/               # Domain model classes
│   ├── RequestModel.cs          # Transport-agnostic request (method, URL, headers, body)
│   ├── ResponseModel.cs         # Transport-agnostic response (status, headers, body, timing)
│   ├── CollectionRequest.cs     # A saved request (with file path, auth config, etc.)
│   ├── CollectionFolder.cs      # A folder of saved requests
│   ├── EnvironmentModel.cs      # Named set of variables ("Environment" is reserved in .NET)
│   ├── EnvironmentVariable.cs   # A single variable (static, dynamic, mock-data, etc.)
│   ├── HistoryEntry.cs
│   └── AuthConfig.cs            # Auth configuration for a request or folder
├── Abstractions/         # Service interfaces
│   ├── ITransport.cs            # Core abstraction — sends a request, returns a response
│   ├── ICollectionService.cs    # Manages request collections (CRUD, auth, ordering)
│   ├── IEnvironmentService.cs   # Manages environments (CRUD)
│   ├── IEnvironmentMergeService.cs # Three-layer env merge (global + active + dynamic)
│   ├── IHistoryService.cs
│   ├── ISecretStorageService.cs # Per-user AES-GCM encrypted local secret store
│   ├── IDynamicVariableEvaluator.cs
│   └── ICollectionImportService.cs
├── Services/             # Implementations of the above interfaces
│   ├── FileSystemCollectionService.cs   # .callsmith file format
│   ├── BrunoCollectionService.cs        # Bruno (.bru) file format
│   ├── RoutingCollectionService.cs      # Delegates to File or Bruno based on collection type
│   ├── FileSystemEnvironmentService.cs
│   ├── BrunoEnvironmentService.cs
│   ├── RoutingEnvironmentService.cs
│   ├── EnvironmentMergeService.cs
│   ├── FileSystemSecretStorageService.cs
│   ├── AesSecretEncryptionService.cs
│   ├── CollectionImportService.cs
│   ├── DynamicVariableEvaluatorService.cs
│   ├── VariableSubstitutionService.cs
│   └── RecentCollectionsService.cs
├── Helpers/              # Internal utilities
│   ├── AesGcmEncryption.cs  # Shared AES-256-GCM encrypt/decrypt/key-management
│   ├── FileSystemHelper.cs
│   └── ResponseFormatter.cs
└── Transports/           # ITransport implementations, one per protocol
    └── Http/
        ├── HttpTransport.cs     # ITransport implementation using HttpClient
        └── Handlers/
            └── TimingHandler.cs # Measures response time
```

#### The `ITransport` abstraction

All network communication goes through this single interface:

```csharp
public interface ITransport
{
    /// <summary>The protocol this transport handles (e.g. "http", "grpc", "websocket").</summary>
    string Protocol { get; }

    Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default);
}
```

The UI and the rest of Core never reference `HttpTransport`, `GrpcTransport`, or any
concrete transport class. They only reference `ITransport`. A `TransportRegistry`
resolves the correct `ITransport` implementation at runtime based on the request type.

**Current implementations:**
- `HttpTransport` — HTTP/REST via `HttpClient` ✅ (Phase 2)

**Planned implementations:**
- `WebSocketTransport` — WebSocket connections (Phase 9)
- `GrpcTransport` — gRPC via `Grpc.Net.Client` (Phase 9)

### Callsmith.Data

Persistence layer. Implements the interfaces defined in Core.

```
Callsmith.Data/
├── CallsmithDbContext.cs    # EF Core DbContext (SQLite)
├── HistoryEntryEntity.cs    # EF Core entity (NOT the same as Core's HistoryEntry)
├── HistoryRepository.cs     # Implements IHistoryService; includes manual schema migration
└── AesHistoryEncryptionService.cs  # AES-256-GCM at-rest encryption for history secrets
```

> **Note:** Collections and environments are stored as plain files on disk
> (per ADR-004), not in SQLite. There are no EF entities or repositories for
> them — `FileSystemCollectionService` and `FileSystemEnvironmentService` in
> `Callsmith.Core` read and write directly to the filesystem.

**Database location at runtime:**

History is stored in a per-collection SQLite database keyed by a SHA-256 hash of
the collection folder path:

| OS | Path |
|---|---|
| Windows | `%APPDATA%\Callsmith\history\<hash>.db` |
| macOS | `~/Library/Application Support/Callsmith/history/<hash>.db` |
| Linux | `~/.config/Callsmith/history/<hash>.db` |

### Callsmith.Desktop

The Avalonia UI shell. Wires everything together.

```
Callsmith.Desktop/
├── App.axaml / App.axaml.cs     # Application entry, DI container setup
├── MainWindow.axaml             # Shell window with tab bar and navigation sidebar
├── Views/                       # AXAML view files (one per ViewModel)
│   ├── CollectionsView.axaml    # Left-panel collections tree
│   ├── EnvironmentEditorView.axaml
│   ├── EnvironmentSelectorView.axaml
│   ├── HistoryPanelView.axaml
│   ├── FolderSettingsDialog.axaml
│   ├── ImportCollectionDialog.axaml
│   ├── CommandPaletteView.axaml
│   └── ...
├── ViewModels/                  # One ViewModel per View
│   ├── MainWindowViewModel.cs
│   ├── RequestTabViewModel.cs   # Main request/response pane (per-tab)
│   ├── CollectionsViewModel.cs
│   ├── EnvironmentEditorViewModel.cs
│   ├── HistoryPanelViewModel.cs
│   ├── FolderSettingsViewModel.cs
│   └── ...
└── Controls/                    # Reusable custom Avalonia controls
    ├── SyntaxEditor.cs          # AvaloniaEdit-based syntax-highlighted editor
    └── KeyValueEditor.axaml     # Generic key/value pair table (headers, params)
```

---

## Key Data Flows

### Sending an HTTP Request

```
User clicks "Send"
    │
    ▼
RequestViewModel.SendAsync(ct)
    │  builds RequestModel from UI state
    │  calls
    ▼
ITransport.SendAsync(request, ct)         [Core]
    │  uses HttpClient under the hood
    │  returns
    ▼
ResponseModel
    │
    ▼
RequestViewModel updates observable properties
    │
    ▼
View re-renders via data binding (status, headers, body, timing)
    │
    ▼ (async, fire-and-forget with own ct)
IHistoryService.SaveAsync(request, response, ct)   [Core → Data]
```

### Loading a Saved Request

```
User clicks a request in the Collections panel
    │
    ▼
CollectionsViewModel selects item, publishes message
    │  (uses CommunityToolkit.Mvvm WeakReferenceMessenger)
    ▼
RequestViewModel receives message, calls
    │
    ▼
ICollectionService.LoadRequestAsync(filePath, ct)    [Core → Filesystem]
    │  returns RequestModel
    ▼
RequestViewModel populates its observable properties
    │
    ▼
View re-renders with loaded request data
```

---

## Cross-Platform Distribution

Published as a **self-contained, single-file executable** — no .NET runtime
installation needed on the target machine.

| Platform | RID | Command |
|---|---|---|
| Windows x64 | `win-x64` | `dotnet publish -r win-x64 -c Release --self-contained` |
| Windows ARM64 | `win-arm64` | `dotnet publish -r win-arm64 -c Release --self-contained` |
| macOS ARM64 | `osx-arm64` | `dotnet publish -r osx-arm64 -c Release --self-contained` |
| macOS x64 | `osx-x64` | `dotnet publish -r osx-x64 -c Release --self-contained` |
| Linux x64 | `linux-x64` | `dotnet publish -r linux-x64 -c Release --self-contained` |

For macOS, an `.app` bundle can be produced using the `dotnet-bundle` tool.
For Windows, an installer can be produced using WiX or MSIX.

---

## Architecture Decision Records (ADRs)

Full ADR documents live in [`docs/adr/`](./docs/adr/).
