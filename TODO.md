# TODO — Callsmith

> This is the living feature backlog. AI agents should read this to understand
> current priorities before starting work. Update this file when features are
> completed or new items are identified.
>
> **Status legend:**
> - `[ ]` — not started
> - `[~]` — in progress
> - `[x]` — done

---

## Core Design Goals

> These are the non-negotiable product principles that every phase must serve.
> AI agents must read these before implementing any feature.

1. **Filesystem-native collections** — requests live on disk as files in a folder
   structure the user owns. No import/export. The tool opens a folder, just like
   a code editor opens a project. Collections can be committed to git and shared
   with a team naturally.

2. **Deep, filterable request history** — every request ever sent is recorded and
   searchable with powerful filters (status code, body content, headers, endpoint
   name, date range, etc.). History is a first-class feature, not an afterthought.

3. **Dynamic environment variables** — environment variables are not just static
   key/value pairs. A variable can be the result of another API call, a JavaScript
   expression, an XPath/JSONPath extraction, or a transformation. This enables
   fully automated auth flows and chained requests.

4. **Swappable environments** — a request collection can have multiple environments
   (dev, staging, production). Switching is a single click. Each environment has
   its own variable set.

5. **Full visibility in one view** — the user can see the final resolved request
   URL, request headers (after variable substitution), request body, response
   status, response headers, and response body all at once without switching tabs.

6. **Native look and feel** — light and dark theme support. Respects the OS theme
   by default. No web runtime, no Electron, no compromises on performance.

---

## Phase 1 — Project Foundation

> Goal: A working solution structure with a runnable (but empty) app window.

- [x] Create the .NET solution file (`Callsmith.sln`)
- [x] Scaffold `Callsmith.Core` class library
- [x] Scaffold `Callsmith.Data` class library
- [x] Scaffold `Callsmith.Desktop` Avalonia application
- [x] Scaffold `Callsmith.Core.Tests` xUnit test project
- [x] Scaffold `Callsmith.Desktop.Tests` xUnit test project
- [x] Set up project references (Desktop → Core, Desktop → Data, Data → Core)
- [x] Add NuGet packages to each project (see ARCHITECTURE.md for the list)
- [x] Set up `.editorconfig` and `Directory.Build.props`
- [x] Confirm `dotnet build` passes
- [x] Confirm `dotnet publish -r win-x64 --self-contained` produces a runnable exe
- [x] Set up GitHub Actions CI — `dotnet build` + `dotnet test` on every push to `main`

---

## Phase 2 — Core HTTP Engine

> Goal: Be able to send a raw HTTP request and receive a response in code (no UI yet).
> Implement the `ITransport` abstraction first so all future transports (gRPC,
> WebSocket, etc.) slot in without touching existing code.

- [x] Define `RequestModel` — transport-agnostic: method/verb, URL, headers, body, timeout
- [x] Define `ResponseModel` — transport-agnostic: status, headers, body, elapsed time, size
- [x] Define `ITransport` interface — `string Protocol { get; }` + `SendAsync(RequestModel, ct)`
- [x] Define `TransportRegistry` — resolves the correct `ITransport` by protocol at runtime
- [x] Implement `HttpTransport` using `HttpClient`
  - [x] Support all common HTTP methods (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS)
  - [x] Capture response headers
  - [x] Capture response body as string and raw bytes
  - [x] Measure elapsed time
  - [x] Handle timeouts via `CancellationToken`
  - [x] Handle redirects (configurable: follow or do not follow)
  - [x] Capture the final resolved URL after redirects
- [x] Register `HttpTransport` in `TransportRegistry` under protocol `"http"` and `"https"`
- [x] Write unit tests for `HttpTransport` (mock the `HttpMessageHandler`)
- [x] Write unit tests for `TransportRegistry`

---

## Phase 3 — Filesystem-Native Collections

> Goal: Requests live on disk. The tool opens a folder; no import or export needed.
> This is the foundational data model — get it right before building any UI.
> Inspired by Bruno's approach.

- [x] Define the on-disk file format for a request (e.g. `.callsmith` or `.json` file per request)
  - Must store: method, URL, headers, body type, body content, description
  - Must be human-readable and git-diff-friendly (plain text / JSON, not binary)
- [x] Define the folder structure convention:
  - A collection is a folder
  - Sub-folders are sub-collections
  - Each request is a single file in the folder
  - An optional `environment/` sub-folder holds environment files
- [x] Define `ICollectionService` interface — open folder, list requests, load request, save request
- [x] Implement `FileSystemCollectionService` — reads/writes directly from disk, no database
- [x] Write unit tests for `FileSystemCollectionService`

---

## Phase 4 — Basic UI Shell

> Goal: A visible, usable app window. Full-visibility layout where the user sees
> everything at once — no tab-switching required to see request and response together.

- [x] Main window layout:
  - Left sidebar: collection folder tree
  - Right pane split vertically: request editor (top) and response viewer (bottom)
  - Both halves visible simultaneously — no tabs hiding information
- [x] Request editor section:
  - Method selector + URL bar + Send button
  - Resolved URL preview (shows URL after variable substitution, always visible)
  - Headers panel (key/value editor, always visible) — implemented in Phase 5
  - Body panel (always visible, collapses gracefully when empty) — implemented in Phase 5
- [x] Response viewer section:
  - Status code badge + elapsed time + response size (always visible)
  - Response headers (always visible, collapsible)
  - Response body with syntax highlighting (always visible)
- [x] Top toolbar: environment switcher — implemented in Phase 6
- [x] Status bar: current collection path, active environment name — deferred to Phase 6
- [x] `RequestViewModel` wired to `ITransport` via `TransportRegistry`
- [x] Send a GET request and see the full response without changing any view
- [x] Collections sidebar: folder tree view reflecting the real filesystem layout
- [x] Open a folder as a collection (folder picker dialog)
- [x] Remember recently opened collection folders
- [x] Create, rename, delete requests and sub-folders in the sidebar
  - Right-click context menu on any node (folder or request)
  - F2 / double-click for inline rename directly in the tree
  - Delete key or context menu → non-modal inline confirmation strip
  - Phantom "new node" appears in tree immediately; Enter creates file, Esc cancels
- [x] Changes on disk (external edits, git pull) are reflected without restart
  - `FileSystemWatcher` with 500 ms debounce; preserves expanded state and selection
- [x] Unsaved-changes navigation guard (inline panel — no modal dialogs)
  - Ctrl+S global keyboard shortcut for Save
  - Save button always visible; dims when clean, highlights when dirty

---

## Phase 5 — Request Editor

> Goal: Full request editing — headers, query params, body, auth.

- [x] Reusable `KeyValueEditor` control (used for headers, query params, env vars)
- [x] Query param editor (syncs bidirectionally with the URL bar)
- [x] Path param editor (in Params section) with URL-template discovery:
  - [x] Detect placeholders in URL path (e.g., `/users/{id}/orders/{orderId}`)
  - [x] Auto-populate path params list from detected placeholders
  - [x] Keep list in sync as URL template changes (add/remove/rename placeholders)
  - [x] Resolve path placeholders at send time before query-string composition
  - [x] Path param values support static text, env variables, or mixed values (e.g., `{{tenant}}-{id}`)
  - [x] Preserve unresolved placeholders in preview/send with clear validation feedback
- [x] Request body editor:
  - [x] Body type selector: None, JSON, plain text, XML, form-urlencoded, multipart
  - [x] Syntax-highlighted editor for JSON and XML body types
- [x] Auth tab: None, Bearer token, Basic auth, API key (header or query param)
- [x] Save request changes back to disk with explicit Save (● dirty indicator in title bar)

---

## Phase 6 — Environments and Dynamic Variables

> Goal: Swappable environments with dynamic, scriptable variables.
> This is a core differentiator — treat it with appropriate depth.
>
> **Phase 6 complete (static + secret variables). Script/chained variables are deferred to a later phase.**

- [x] Define the on-disk environment file format (`.env.callsmith` JSON files in `environment/` folder)
- [x] Define `EnvironmentVariable` model:
  - [x] Static value (plain string)
  - [ ] Script value (JavaScript expression evaluated at request time) — **deferred (needs Jint)**
  - [x] Chained value (result of another request + JSONPath/XPath extraction) — **deferred**
- [x] Define `IEnvironmentService` interface
- [x] Implement `FileSystemEnvironmentService`
- [x] Environment selector UI toolbar strip (environment switcher — single click to swap)
- [x] Variable substitution engine:
  - [x] Replace `{{variableName}}` in URL, headers, and body at send time
  - [ ] Evaluate JavaScript expressions — **deferred**
  - [x] Execute chained requests
- [x] Environment editor UI (full CRUD from within the app):
  - [x] List environments (dev, staging, production, etc.)
  - [x] Add, rename, delete environments
  - [x] Per-variable: choose type (static), edit value
  - [x] Secret variables — masked in UI, excluded from logs, never written to history in plaintext
  - [x] Resolved variable values shown in the environment editor:
    - [x] Mock data and response-body variables display cached preview values
    - [x] Static variables that reference dynamic variables show substituted values
    - [x] Cascading updates when dynamic variable config changes
- [x] Write unit tests for the substitution engine and file service

---

## Phase 7 — Request History

> Goal: Every request ever sent is recorded and searchable with powerful filters.
> History is a first-class feature, not a log.

- [x] Define `HistoryEntry` model:
  - Timestamp, method, URL, request headers, request body
  - Response status code, response headers, response body, elapsed time
  - Collection name and request name (if sent from a saved request)
- [x] Define `IHistoryService` interface
- [x] Implement `HistoryRepository` backed by SQLite (history is too large for flat files)
- [x] Auto-record every sent request+response pair
- [x] History panel with columns: timestamp, method, URL, status code, elapsed time
- [x] Filter history by:
  - [x] Status code or status code range (e.g. `>= 400`, `= 200`)
  - [x] Response body contains string
  - [x] Request or response header name/value (e.g. `X-Correlation-Id = abc123`)
  - [x] Request name or collection name
  - [ ] URL pattern (contains, starts with, regex)
  - [x] Date range
- [x] Combine multiple filters at once
- [x] Click a history entry to open the full request/response in a read-only detail view
- [x] Re-send a history entry (loads it into the request editor)
- [x] Clear history: all, or entries older than N days
- [x] Write unit tests for filter logic

---

## Phase 8 — UI Polish and Usability

> Goal: Dedicate a full phase to iterative visual quality and UX improvements.
> Work item style: small, observable UI fixes shipped continuously.

- [ ] Establish a UI polish checklist and triage labels (contrast, spacing, sizing, discoverability, keyboard)
- [ ] Contrast pass across all primary screens:
  - [ ] Verify text/background contrast in dark theme for headers, tabs, dropdowns, placeholders, and status text
  - [ ] Ensure selected/focused/disabled states remain readable
  - [ ] Audit warning/error/success colors for clarity and consistency
- [ ] Consistency pass:
  - [ ] Normalize control sizing (buttons, dropdowns, tabs, inputs) across views
  - [ ] Align spacing scale (margins/padding/gaps) and border radii
  - [ ] Standardize typography scale for labels/body/metadata
- [ ] Usability pass:
  - [ ] Reduce friction for common actions (send, save, switch environment, open collection)
  - [ ] Improve empty states and inline guidance text
  - [ ] Improve error message clarity and placement
- [ ] Interaction pass:
  - [ ] Validate hover/focus/pressed states for all interactive controls
  - [ ] Ensure keyboard navigation order is logical in all primary panes
  - [ ] Add or refine keyboard shortcuts for high-frequency actions
- [ ] Resizable layout pass:
  - [ ] Verify usability at small window sizes
  - [ ] Verify split panes and toolbars remain usable under constrained widths
- [ ] Cross-platform visual sanity checks (Windows/macOS/Linux)
- [ ] Add/expand UI tests for critical interaction flows where practical

---

## Phase 9 — Response Viewer Polish

> Goal: Make reading responses fast and pleasant.

- [ ] Syntax-highlighted JSON viewer with collapsible tree + raw toggle
- [ ] XML formatter and viewer
- [ ] HTML preview (rendered in a sandboxed view)
  - Use `Avalonia.WebView` (wraps WebView2/WKWebView/WebKitGTK per platform)
  - Must intercept navigation and block external resource loads by default
  - Requires a "Preview" toggle — raw formatted body and rendered view side by side
  - AngleSharp (already a dependency) can sanitise the DOM before handing to the webview if needed
- [ ] Binary response handling (show MIME type and size, offer save-to-file)
- [ ] Response header descriptions on hover (common headers explained inline)
- [ ] Copy response body to clipboard button
- [ ] Response diff viewer — compare any two history entries side by side

---

## Phase 10 — Additional Transports and Advanced Features

- [ ] `WebSocketTransport` — implement `ITransport` for WebSocket connections
  - [ ] Connect, send messages, receive a stream of messages
  - [ ] WebSocket-specific UI panel (message log, send field)
- [ ] `GrpcTransport` — implement `ITransport` for gRPC via `Grpc.Net.Client`
  - [ ] Support unary, server-streaming, and client-streaming calls
  - [ ] Proto file loading for method/message discovery
  - [ ] gRPC-specific UI panel
- [ ] Cookie jar — view, edit, and persist cookies per domain
- [x] Insomnia collection import (`ICollectionImporter` abstraction, `InsomniaCollectionImporter`, sidebar import button)
  - Parses Insomnia v5 YAML (schema_version 5.x): requests, nested folders, environments + sub-environments
  - Normalises Insomnia Nunjucks variable syntax (`{{ _['var'] }}`) to Callsmith `{{var}}` format
  - Script-valued env vars (`{% response … %}`) are skipped on import (unsupported)
- [x] Postman collection import (`PostmanCollectionImporter`) — parses Postman v2.1 JSON collections, maps requests, folders, and environments to Callsmith format
- [ ] OpenAPI / Swagger file import — generates request files on disk from a spec
- [x] Request chaining in collections — define a sequence of requests, pass values between them

---

## Phase 11 — Distribution and Polish

- [x] Application icon and branding
- [ ] Settings screen: default timeout, font size, proxy configuration
- [ ] Light and dark theme, respects OS default, manual override in settings
- [ ] Keyboard shortcuts for all primary actions (send, save, switch environment, open collection)
- [ ] Windows installer (MSIX or WiX)
- [ ] macOS `.app` bundle
- [x] GitHub Actions CD — publish release artifacts on version tag
