# AGENTS.md — AI Agent Development Instructions

> This file is the **primary instruction set** for any AI coding agent working in
> this repository — GitHub Copilot, Cursor, Claude, GPT-4, etc.
>
> **Always read this file before writing or modifying any code.**
>
> This project uses an **AI-first development workflow**: AI agents are used to
> *build* this application. The application itself has no AI features whatsoever.

---

## 1. Project Identity

**Name:** Callsmith  
**Type:** Native cross-platform desktop application  
**Domain:** Developer tooling — HTTP API client (like Postman)  
**Language:** C# 13  
**Runtime:** .NET 10 (LTS)  
**UI Framework:** Avalonia UI  

The end user needs nothing except the application binary. No internet connection
required, no API keys, no account, no telemetry.

---

## 2. Repository Layout

Always respect this structure. Do not create files outside of it without being
explicitly asked.

```
callsmith/
├── src/
│   ├── Callsmith.Desktop/        # Avalonia UI — views, view models, app entry point
│   ├── Callsmith.Core/           # Domain logic: HTTP engine, models, services
│   └── Callsmith.Data/           # EF Core DbContext, migrations, repositories
├── tests/
│   ├── Callsmith.Core.Tests/     # Unit tests for Core
│   └── Callsmith.Desktop.Tests/  # ViewModel and UI tests
├── docs/                         # Architecture diagrams, ADRs, design notes
├── .github/workflows/            # CI/CD pipelines
├── AGENTS.md                     # ← You are here
├── ARCHITECTURE.md               # System design
├── CONVENTIONS.md                # Coding conventions
└── TODO.md                       # Prioritized feature backlog
```

### Project Responsibilities

| Project | What belongs here |
|---|---|
| `Callsmith.Desktop` | AXAML views, ViewModels, navigation, app startup, DI wiring. No business logic. |
| `Callsmith.Core` | All business logic. HTTP engine, request/response models, collection management, environment management, history. No UI references. |
| `Callsmith.Data` | Persistence only. EF Core DbContext, entity models, migrations, repository implementations. |

---

## 3. Technology Decisions (Do Not Override)

These are final. Do not suggest or introduce alternatives.

| Concern | Decision | Do NOT use |
|---|---|---|
| UI Framework | **Avalonia UI** | WPF, WinForms, MAUI, Electron, Tauri |
| MVVM | **CommunityToolkit.Mvvm** | Prism, ReactiveUI, hand-rolled |
| HTTP | **System.Net.Http.HttpClient** | RestSharp, Flurl, Refit |
| ORM | **EF Core + SQLite** | Dapper, NHibernate, external DB servers |
| DI | **Microsoft.Extensions.DependencyInjection** | Autofac, Ninject, Castle Windsor |
| Serialization | **System.Text.Json** | Newtonsoft.Json / Json.NET |
| Testing | **xUnit + FluentAssertions + NSubstitute** | MSTest, Moq, Shouldly |
| Logging | **Microsoft.Extensions.Logging** | Serilog, NLog (may add sinks later, but not the abstraction) |

---

## 4. Layer Rules

### What goes in Core

- Interfaces for all services (`ITransport`, `ICollectionService`, etc.)
- Domain model classes (`RequestModel`, `ResponseModel`, `RequestCollection`, etc.)
- Business logic implementations of those interfaces
- No references to Avalonia, EF Core, or any I/O framework

### What goes in Data

- Implements the repository/service interfaces defined in Core
- EF Core entities (separate classes from Core domain models — map between them)
- The SQLite database file path is resolved at runtime:
  - Windows: `%APPDATA%\Callsmith\data.db`
  - macOS: `~/Library/Application Support/Callsmith/data.db`
  - Linux: `~/.config/Callsmith/data.db`

### What goes in Desktop

- One ViewModel per View
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm
- ViewModels call Core service interfaces — never Data or EF Core directly
- Views are AXAML only; code-behind should be near-empty (only things that
  genuinely cannot be done in AXAML bindings)

---

## 5. Coding Rules

> These supplement CONVENTIONS.md. Both must be followed.

1. **No business logic in ViewModels.** ViewModels coordinate UI state and delegate
   to Core services. They do not implement logic themselves.

2. **The UI is a guest.** `Callsmith.Core` is the entire application.
   `Callsmith.Desktop` is one possible front-end. Every feature must be fully
   reachable through Core's public service interfaces without any UI present.
   If you cannot call a feature from a unit test or a hypothetical CLI project
   without referencing `Callsmith.Desktop`, the architecture is wrong.

3. **All I/O is async.** Any method that performs HTTP, file, or database work must
   be `async Task<T>` and properly awaited. Never use `.Result`, `.Wait()`, or
   `GetAwaiter().GetResult()`.

4. **Cancellation tokens everywhere.** Every async method that does I/O must accept
   a `CancellationToken ct = default` parameter and pass it through.

5. **Nullable reference types are enabled.** Every project has
   `<Nullable>enable</Nullable>`. Handle nulls explicitly — no `!` suppression
   unless genuinely unavoidable with a comment explaining why.

6. **No magic strings.** Use `const` fields, `enum`, or `static readonly` for
   repeated string values. Exception: HTTP method names use `HttpMethod.Get` etc.

7. **One class per file.** The filename must match the class/interface/enum name.

8. **Interfaces before implementations.** Define the interface in Core first, then
   implement it. This keeps the architecture honest and testable.

9. **Tests are required** for all Core and Data logic before a feature is
   considered complete.

---

## 6. How to Implement a New Feature

Follow this checklist in order:

- [ ] 1. **Read TODO.md** — confirm the feature is listed and prioritized
- [ ] 2. **Define the domain model** — add/update model classes in `Callsmith.Core/Models/`
- [ ] 3. **Define the service interface** — add it to `Callsmith.Core/Abstractions/`
- [ ] 4. **Implement the service** — add implementation in `Callsmith.Core/Services/`
- [ ] 5. **Implement persistence** (if needed) — add repository in `Callsmith.Data/`
- [ ] 6. **Write unit tests** — add to `Callsmith.Core.Tests/`
- [ ] 7. **Build the ViewModel** — add to `Callsmith.Desktop/ViewModels/`
- [ ] 8. **Build the View** — add AXAML to `Callsmith.Desktop/Views/`
- [ ] 9. **Register in DI** — wire up new services in `App.axaml.cs` or a `ServiceCollectionExtensions` class
- [ ] 10. **Update TODO.md** — mark done, add follow-up items if any

---

## 7. Git Conventions

- **Branch names:** `feature/short-description` or `fix/short-description`
- **Commit messages** follow [Conventional Commits](https://www.conventionalcommits.org/):
  - `feat: add response diff viewer`
  - `fix: correct timeout handling in HTTP engine`
  - `docs: update AGENTS.md with layer rules`
  - `test: add unit tests for CollectionService`
  - `chore: update Avalonia to 11.x`
- Do **not** commit: secrets, API keys, `.env` files, `bin/`, `obj/`

---

## 8. What to Do When Uncertain

If you are unsure about a decision:

1. Check **ARCHITECTURE.md** for structural intent
2. Check **CONVENTIONS.md** for style rules
3. Check **TODO.md** for current priorities
4. **Surface the ambiguity as a question** rather than making an assumption
5. **Prefer doing less** over doing the wrong thing — a partial correct
   implementation is better than a complete incorrect one

---

## 9. What This Project Is NOT

To keep agents from going off-track:

- ❌ This is NOT a web app. Do not create any ASP.NET, Blazor, or JS/TS files.
- ❌ This is NOT an Electron or Tauri app. Do not create any web frontend files.
- ❌ This app has NO AI features. Do not add OpenAI, Semantic Kernel, LangChain,
  or any AI SDK as a dependency to any project in this solution.
- ❌ This app requires NO internet connection to function. Do not add cloud SDKs.
- ❌ Do not add telemetry, analytics, or crash reporting without explicit instruction.
