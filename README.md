# 🔨 Callsmith

**A fast, local-first HTTP client built for developers who care about their tools.**

No accounts. No cloud sync. No browser runtime dressed up as a desktop app. Just a
native, keyboard-friendly API client that gets out of your way and lets you work.

> ⚠️ Pre-release — core features are implemented and stable. The project is actively
> developed. Feedback and contributions are welcome.

---

## ✨ Why Callsmith?

### ⚡ Actually fast. Actually native.

Callsmith is compiled C# running on .NET 10, rendered by Avalonia's Skia pipeline
directly on the OS. It starts in under a second, uses a fraction of the RAM you're
used to, and never stutters under a large response body. This is what a desktop app
is supposed to feel like.

### 🔍 Find anything instantly — `Ctrl+P` and `Alt+R`

Open the **command palette** with `Ctrl+P` to fuzzy-search every request in your
collection and jump to it instantly — without touching the mouse. Already editing a
request and lost track of where it lives in the tree? Hit `Alt+R` to instantly reveal
and highlight it in the sidebar. These aren't afterthoughts — they're the primary
navigation model.

### 📋 Everything visible, nothing hidden

The request editor and the response viewer are always visible together. Headers,
query params, and path params all live on the same **Params** tab — no separate tabs
to juggle for each. The resolved URL (after variable substitution) is always visible
below the URL bar.

The layout is flexible too: drag the split between request and response to wherever
works for you, resize the sidebar, and go horizontal or vertical depending on your
screen and workflow.

### 🌍 Environments that keep you safe

Color-code your environments so **dev, staging, and production are visually distinct**
at all times. Reorder them however makes sense to your team. Switch with a single click
from the toolbar. The active environment name is always visible — you will never
accidentally fire a production request because you forgot to switch back.

**Environments are dynamic, not static.** A variable can hold a plain value, a
mocked data pattern, or an extraction from a prior response — JSONPath, XPath, and
regex are all supported. You don't need pre/post-request scripts to thread auth tokens
between calls — you just describe what value you want, and Callsmith resolves it at
send time. Variables are substituted across the URL, every header, and the full
request body. Secret variables are masked in the UI and never written to history in
plaintext.

### 📜 Request history as a first-class feature

Every request you send is recorded automatically — timestamp, method, URL, request
headers and body, response status, response headers, response body, elapsed time, and
the collection and request it came from.

This isn't a log dump. It's a **searchable, filterable archive**:

- Filter by status code or range (`>= 400`, `= 200`)
- Filter by date range
- Filter by response body content
- Filter by header name/value (`X-Correlation-Id = abc123`)
- Filter by request or collection name
- Combine multiple filters at once

Click any entry to open the full request and response in a read-only detail view —
including the exact headers and body *as sent*, not just as configured. Re-send any
historical request with one click. You will never lose a request that worked, even
if you forgot to save it to a collection.

### 🗂️ Collections that live on your filesystem

A Callsmith collection is just a folder. Requests are plain files on disk. Open a
folder, start working. Commit to git, share with teammates, diff in your code review
tool — no export step, no proprietary binary format, no workspace lock-in.

Collections are watched for external changes too. Edit a file in your code editor or
pull new files from git, and the sidebar updates without a restart.

**Already using Bruno?** You don't need to import or convert a thing. Just point
Callsmith at your existing Bruno collection folder — it reads `.bru` files natively.
Your teammates keep using Bruno, you use Callsmith, and everyone works off the same
files on disk without any friction or format negotiation.

**Switching from Postman or Insomnia, or have an OpenAPI/Swagger spec?** Import it as
a new collection or merge it into an existing one. When a new version of a spec ships,
import it additively — new endpoints appear alongside your existing requests without
touching your saved edits, dynamic variables, or custom environments. No more manually
merging collections or stomping on work to pick up an upstream change.

### 🏗️ An architecture you can trust

Callsmith is built in three clean layers: **Core** (pure C# business logic, no UI
references), **Data** (SQLite persistence via EF Core), and **Desktop** (Avalonia UI
shell). The core engine is fully operable from tests or a future CLI with zero UI
involvement.

The HTTP engine is built on `System.Net.Http.HttpClient` behind a transport
abstraction — gRPC and WebSocket transports will slot in without changing the request
pipeline or the UI. Every layer has unit tests.

---

## 🎯 What You Get Today

All of these are implemented now, not roadmap items:

| Feature | Details |
|---|---|
| 🌐 HTTP methods | GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS |
| 📝 Request editor | Method, URL, headers, query params, path params, body, auth |
| 📦 Body types | None, JSON, plain text, XML, YAML, form-urlencoded, multipart, binary file |
| 🔐 Auth types | None, Bearer token, Basic, API key (header or query param) |
| ✅ Response viewer | Status, elapsed time, size, headers, syntax-highlighted body |
| 📁 Collections | Filesystem-native, git-friendly, watched for external changes |
| 🌍 Environments | Color-coded, reorderable, dynamic variables, secret masking |
| 🔁 Dynamic variables | Extract values from prior responses via JSONPath, XPath, or regex |
| 🎲 Mock data editor | Rich built-in generator for names, emails, UUIDs, dates, numbers, addresses, lorem ipsum, and more |
| 📜 Request history | Full archive, multi-filter search, re-send from history |
| 📥 Collection import | Postman v2.1, Insomnia v5, OpenAPI/Swagger — create new or merge into existing |
| 🗂️ Native Bruno support | Open Bruno collection folders directly — no import or conversion needed |
| ⌨️ Keyboard-first | `Ctrl+P` palette, `Alt+R` reveal, `Ctrl+Enter` send, `Ctrl+S` save |
| 🔄 Live disk sync | External edits and git pulls reflected without restart |

---

## ⌨️ Keyboard Shortcuts

### Global

| Shortcut | Action |
|---|---|
| `Ctrl+P` | Open command palette — fuzzy-search and jump to any request |
| `Alt+R` | Reveal active request in the collections sidebar |
| `Ctrl+Enter` | Send request |
| `Ctrl+S` | Save current context (active request tab or environment editor) |

### Collections Tree

| Shortcut | Action |
|---|---|
| `F2` | Rename selected request or folder |
| `Delete` | Delete selected request or folder |
| `Enter` | Confirm rename or create dialog |
| `Escape` | Cancel rename or create dialog |

### Command Palette

| Shortcut | Action |
|---|---|
| `↑` / `↓` | Move selection |
| `Enter` | Execute selected command |
| `Escape` | Close palette |

### Environment Variable Completion

| Shortcut | Action |
|---|---|
| `↑` / `↓` | Move suggestion selection |
| `Enter` / `Tab` | Insert selected variable |
| `Escape` | Close suggestion popup |

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- No accounts, API keys, or cloud services required

### Run Locally

```bash
git clone https://github.com/callsmith/callsmith
cd callsmith
dotnet restore
dotnet run --project src/Callsmith.Desktop
```

### Build a Self-Contained Executable

```bash
# Windows (x64)
dotnet publish src/Callsmith.Desktop -r win-x64 -c Release --self-contained

# macOS (Apple Silicon)
dotnet publish src/Callsmith.Desktop -r osx-arm64 -c Release --self-contained

# Linux (x64)
dotnet publish src/Callsmith.Desktop -r linux-x64 -c Release --self-contained
```

The output is a single, self-contained binary. No .NET installation required on the
target machine.

---

## 🗺️ Roadmap

Coming next:

- 🌲 Syntax-highlighted JSON tree viewer with collapsible nodes + raw toggle
- 📊 Response diff viewer — compare any two history entries side by side
- 🔌 WebSocket and gRPC transports
- ⚙️ Settings screen (timeout, font size, proxy, theme override)
- 📦 Windows installer and macOS `.app` bundle

---

## 🔧 Project Structure

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

---

## 🤝 Contributing

See [AGENTS.md](./AGENTS.md) for AI agent development instructions.
See [CONVENTIONS.md](./CONVENTIONS.md) for coding standards.
See [ARCHITECTURE.md](./ARCHITECTURE.md) for system design decisions.
