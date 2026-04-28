# Plan — Global Undo/Redo (Memento Pattern)

> **Feature:** Session-scoped, global undo/redo stack. Every user edit to a request tab or
> environment editor is tracked as a pair of before/after state snapshots (mementos).
> Ctrl+Z and Ctrl+Y walk the stack; applying an undo/redo also auto-navigates to the
> affected context and re-evaluates dirty state.

---

## Scope of Tracked Actions

**In scope:**
- Request tab edits: URL, method, name/description, headers, query params, path params, body type/content, auth settings
- Environment variable changes (add, edit, delete rows) per `EnvironmentListItemViewModel`

**Out of scope (first pass):**
- Request/folder rename, move, delete (file-system mutations)
- Environment rename/create/delete
- Active environment selection change
- Splitter positions, layout preferences
- Response state (not user-editable)

---

## Architecture Summary

```
Callsmith.Core
  Abstractions/
    IUndoableAction.cs        ← new
    IUndoRedoService.cs       ← new
  Services/
    UndoRedoService.cs        ← new

Callsmith.Desktop
  ViewModels/
    MainWindowViewModel.cs    ← add UndoCommand / RedoCommand, dispatch
    RequestTabViewModel.cs    ← add snapshot capture + ApplySnapshot()
    EnvironmentListItemViewModel.cs  ← add snapshot capture + ApplySnapshot()
    RequestEditorViewModel.cs ← add ApplyUndoRedoToTab() helper
    EnvironmentEditorViewModel.cs   ← add ApplyUndoRedoToEnvironment() helper
  Messages/
    UndoRedoNavigationMessage.cs    ← new
  Actions/
    RequestTabMementoAction.cs      ← new
    EnvironmentMementoAction.cs     ← new
  Controls/
    SyntaxEditor.cs           ← disable Document.UndoStack on init and ApplySnapshot
  Views/
    MainWindow.axaml          ← add KeyBindings for Ctrl+Z / Ctrl+Y
  App.axaml                   ← global TextBox UndoLimit=0 style
  App.axaml.cs                ← register UndoRedoService as singleton
```

---

## Step 1 — Core Abstractions

**`IUndoableAction` (Core/Abstractions)**

A value-typed description of a reversible change:
- `string Description` — human-readable name shown in a potential future undo history tooltip
- `string ContextType` — discriminator constant (`"request"` | `"environment"`)
- Context identity payload (opaque from the service's perspective; inspected by the Desktop dispatch layer)

**`IUndoRedoService` (Core/Abstractions)**

Session-scoped interface:
- `bool CanUndo { get; }`, `bool CanRedo { get; }`
- `string? UndoDescription { get; }`, `string? RedoDescription { get; }` (for tooltip/button label)
- `void Push(IUndoableAction action)` — adds to the undo stack; clears the redo stack
- `IUndoableAction? Undo()` — pops the undo stack, pushes onto the redo stack, returns the action (or null)
- `IUndoableAction? Redo()` — pops the redo stack, pushes onto the undo stack, returns the action (or null)
- `event EventHandler? StackChanged` — fired after every push/undo/redo so command CanExecute can refresh
- `void Clear()` — called when a collection is closed to reset the session stack

---

## Step 2 — Core Implementation

**`UndoRedoService` (Core/Services)**

- Two `Stack<IUndoableAction>` fields: `_undoStack`, `_redoStack`
- `Push` clears `_redoStack`, then pushes onto `_undoStack`; fires `StackChanged`
  - **Stack depth cap:** the undo stack is limited to **200 entries**. When the limit is reached, the oldest entry (bottom of the stack) is discarded before pushing the new one. This bounds memory usage while giving ample undo depth for normal use.
- `Undo` pops `_undoStack`, pushes to `_redoStack`, fires `StackChanged`, returns the action
- `Redo` pops `_redoStack`, pushes to `_undoStack`, fires `StackChanged`, returns the action
- Properties derive from stack counts; service has no dependency on Domain types

---

## Step 3 — Concrete Memento Action Types (Desktop/Actions)

**`RequestTabMementoAction`**
- `string ContextType => "request"`
- `Guid TabId` — identifies the in-session tab instance (hint if the tab is still open)
- `string FilePath` — full path of the backing `.callsmith` file (used to re-open the tab if closed)
- `CollectionRequest Before` — full snapshot before the edit
- `CollectionRequest After` — full snapshot after the edit
- `string Description` (e.g. `"Edit URL"`, `"Edit headers"`, `"Edit body"`)

**`EnvironmentMementoAction`**
- `string ContextType => "environment"`
- `Guid EnvironmentId`
- `string FilePath` — path of the environment file (for navigation + re-open)
- `EnvironmentModel Before`
- `EnvironmentModel After`
- `string Description` (e.g. `"Edit variable"`)

---

## Step 4 — Snapshot Capture with Debounce

The debounce strategy is a **1.5-second coarse-grained capture**. This groups rapid keystrokes into a single undo step while still giving the user fine enough control.

### Per `RequestTabViewModel`

1. Add a private field `_undoBaseline: CollectionRequest?` — initialized from `_sourceRequest` when the tab loads (and reset after each undo/redo or debounce flush).
2. Add a `DispatcherTimer _undoDebounceTimer` (1500 ms, one-shot).
3. In `RecomputeDirtyState()` (already called on every property change):
   - If `_loading` or `_saving`, do nothing.
   - If `_undoBaseline == null`, return (tab not yet ready).
   - Restart the debounce timer.
4. When the timer fires:
   - Build the current state snapshot (`BuildRequestState(includeBlankRows: true)`).
   - Compare against `_undoBaseline` using `CollectionRequestEqualityComparer`.
   - If different, push a `RequestTabMementoAction` onto the service with `Before = _undoBaseline`, `After = current`.
   - Update `_undoBaseline = current`.
5. Add `ApplySnapshot(CollectionRequest snapshot)` method:
   - Suppresses debounce timer.
   - Calls the existing load path (same code used when reloading from disk).
   - Updates `_undoBaseline = snapshot`.
   - Calls `RecomputeDirtyState()` to re-evaluate `HasUnsavedChanges`.

### Per `EnvironmentListItemViewModel`

1. Add `_undoBaseline: EnvironmentModel?` — initialized in the constructor from the loaded `_model`.
2. Add a `DispatcherTimer _undoDebounceTimer` (1500 ms, one-shot).
3. In `OnAnyVariableChanged()` (already called on every variable change):
   - Restart the debounce timer.
4. When the timer fires:
   - Build `BuildModel(includeBlankVariables: true)` (existing method).
   - Compare against `_undoBaseline` using `EnvironmentModelEqualityComparer`.
   - If different, push an `EnvironmentMementoAction`.
   - Update `_undoBaseline`.
5. Add `ApplySnapshot(EnvironmentModel snapshot)` method:
   - Calls the existing `LoadVariables(snapshot.Variables)` path; also restores `Color` and `Name` if applicable.
   - Updates `_undoBaseline = snapshot`.
   - Calls `RecomputeDirtyState()` to re-evaluate `IsDirty`.

> **Important:** The undo/redo debounce timer must be stopped and the current in-progress edit must
> be flushed *before* applying an undo/redo action. Otherwise the in-progress debounce can
> overwrite a just-applied undo.

---

## Step 4a — Disable Control-Level Undo Stacks

### Why this is required

Window-level `KeyBindings` are evaluated during the **bubble phase** of `KeyDown`. Both Avalonia
`TextBox` and AvaloniaEdit `TextEditor` consume Ctrl+Z and mark `e.Handled = true` when their
own undo stack is non-empty — which means the event **never reaches the Window**, and our global
`UndoCommand` never fires. The two controls in the app with their own undo stacks are:

- **`TextBox`** — every URL bar, KVP key/value field, name/description field, and environment
  variable cell.
- **`SyntaxEditor` (extends `TextEditor`)** — the request body editor. Each `TextDocument` has an
  `UndoStack` that records fine-grained character-level operations.

### Resolution: disable both stacks

**`TextBox` — global `App.axaml` style:**

Add one application-level style so no `TextBox` anywhere accumulates undo history:

```xml
<Style Selector="TextBox">
  <Setter Property="UndoLimit" Value="0"/>
</Style>
```

When `UndoLimit` is `0`, no history is recorded, `CanUndo` is always false, the Ctrl+Z keystroke
is not consumed, and the event bubbles to the Window. This is a single-line change that covers
every present and future `TextBox` without needing to touch individual AXAML files.

**`SyntaxEditor` — `SyntaxEditor.cs` constructor and `OnPropertyChanged`:**

After the base `TextEditor` is initialized, set `Document.UndoStack.SizeLimit = 0` to prevent
the AvaloniaEdit document from recording any undo operations. The same reset must also be applied
whenever the `DocumentProperty` changes (AvaloniaEdit creates a fresh `TextDocument` on property
change, which would reset the limit), and in `ApplySnapshot()` when the document content is
replaced programmatically.

Specifically:
1. In the `SyntaxEditor` constructor, after the base initialization:
   `Document.UndoStack.SizeLimit = 0;`
2. In `OnPropertyChanged`, when `change.Property == DocumentProperty`:
   `Document?.UndoStack.SizeLimit = 0;` (already has a guard block there for folding manager)
3. In `OnEditorTextChanged`, no change needed — the size limit already prevents accumulation.

### UX tradeoff (deliberate)

Disabling control-level undo means that edits made **within the 1.5-second debounce window** are
not yet undoable: no snapshot has been committed yet, and the TextBox/SyntaxEditor has no local
history either. If the user presses Ctrl+Z immediately after typing a single character, nothing
happens until the debounce fires and the first snapshot is committed. This is an accepted
coarser-granularity tradeoff; the undo granularity matches the debounce interval, not individual
keystrokes.

### What was ruled out

- **Tunnel-phase Window handler** (`AddHandler(..., RoutingStrategies.Tunnel)`) — would intercept
  Ctrl+Z even inside dialogs (e.g., while fixing a typo in a SaveAs dialog TextBox), causing the
  global undo to fire in contexts where the user expects local text editing. Rejected.
- **Two-tier coexistence** (let controls undo locally until exhausted, then escalate) — creates
  an unpredictable experience: pressing Ctrl+Z many times undoes individual characters, then
  suddenly navigates to a different tab. Rejected.

---

## Step 5 — Dispatch and Navigation in `MainWindowViewModel`

**New commands:**
- `UndoCommand` (RelayCommand, CanExecute: `_undoRedoService.CanUndo`)
- `RedoCommand` (RelayCommand, CanExecute: `_undoRedoService.CanRedo`)
- Both call a private `PerformUndo()` / `PerformRedo()` method.

**`PerformUndo()` flow:**

1. Call `_undoRedoService.Undo()` → returns an `IUndoableAction`.
2. Dispatch on `action.ContextType`:

   **Request tab (`RequestTabMementoAction`, apply `Before` snapshot):**
   a. Flush any pending debounce timer on the matching tab (if the tab is currently open).
   b. Navigate to the request tab: check `RequestEditor.Tabs` for one matching `action.FilePath`.
      - If the tab is open, activate it.
      - If the tab is **not** open (including *new/unsaved* tabs that were closed), re-open it by sending a `RequestSelectedMessage` with `action.FilePath`. This ensures that accidentally-closed new requests can be recovered via Ctrl+Z.
   c. Close the environment editor if open (send `CloseEnvironmentEditorMessage`).
   d. Call `tab.ApplySnapshot(action.Before)`.

   **Environment (`EnvironmentMementoAction`, apply `Before` snapshot):**
   a. Flush any pending debounce timer on the matching environment VM.
   b. Navigate to the environment editor: send `OpenEnvironmentEditorMessage` (or call `EnvironmentEditor.SelectEnvironmentByIdAsync(action.EnvironmentId)`).
   c. Call `environmentVm.ApplySnapshot(action.Before)`.

3. After navigation + apply, `CanUndo`/`CanRedo` refresh automatically via `StackChanged`.

**`PerformRedo()` flow:**

Same as Undo but applies `action.After` snapshot instead of `action.Before`.

**Dirty detection after undo/redo:**

Because `ApplySnapshot` always calls `RecomputeDirtyState()`, this is automatic. If the restored snapshot matches the persisted baseline (`_sourceRequest` / `_model`), dirty is cleared; otherwise it remains set.

**Stack reset on collection close:**

Subscribe to `CollectionOpenedMessage` in `MainWindowViewModel` (or directly in `UndoRedoService`) and call `Clear()` when a new collection is opened.

---

## Step 6 — Navigation Message

**`UndoRedoNavigationMessage.cs` (Desktop/Messages)**

- `string ContextType`
- `Guid EnvironmentId` (for environment actions)
- `string FilePath` (for both types)

Used internally by the dispatch logic. `EnvironmentEditorViewModel` (which already handles `OpenEnvironmentEditorMessage`) is extended to accept an environment ID so the correct row is pre-selected.

---

## Step 7 — Keyboard Binding

In `MainWindow.axaml`, add top-level `KeyBindings` to the Window:

```xml
<Window.KeyBindings>
  <KeyBinding Gesture="Ctrl+Z" Command="{Binding UndoCommand}" />
  <KeyBinding Gesture="Ctrl+Y" Command="{Binding RedoCommand}" />
  <KeyBinding Gesture="Ctrl+Shift+Z" Command="{Binding RedoCommand}" />
</Window.KeyBindings>
```

These bindings respect `CanExecute` automatically (Avalonia disables them when CanExecute is
false). They only fire reliably because control-level undo stacks are disabled (see Step 4a).

---

## Step 8 — DI Registration

In `App.axaml.cs`, `ConfigureServices()`:
- Register `IUndoRedoService` as a **singleton** `UndoRedoService` — session-scoped, shared across all ViewModels.
- Inject into `MainWindowViewModel`.
- `RequestTabViewModel` receives it via the constructor in `RequestEditorViewModel.CreateTab(...)`.
- `EnvironmentListItemViewModel` receives it when constructed by `EnvironmentEditorViewModel`.

---

## Step 9 — Tests

**`Callsmith.Core.Tests` — `UndoRedoServiceTests`:**
- Push/undo/redo sequencing
- Redo stack is cleared on push
- `CanUndo`/`CanRedo` reflect stack state
- `Clear()` empties both stacks
- `StackChanged` event fires on push/undo/redo
- `Undo()`/`Redo()` on empty stack returns null without throwing

**`Callsmith.Desktop.Tests`:**
- `RequestTabViewModelUndoTests`: property change → debounce fires → correct before/after recorded; `ApplySnapshot` restores state and re-evaluates dirty
- `EnvironmentListItemViewModelUndoTests`: variable change → debounce fires → memento recorded; `ApplySnapshot` restores variables and re-evaluates dirty
- `MainWindowViewModelUndoRedoDispatchTests`: `UndoCommand` applies correct snapshot and navigates (mocked child VMs)

---

## Decisions

All outstanding questions have been resolved:

1. **Debounce interval** — **1.5 seconds** is confirmed. No change needed.
2. **Undo scope for new/unsaved tabs** — Closed tabs **must be re-opened** by undo. If a user opened a New Request, made edits, then accidentally closed the tab, pressing Ctrl+Z should reopen it at the `Before` snapshot state. The dispatch layer handles this by always re-opening the file (new or saved) when the tab is not found in the current tab list (see Step 5).
3. **Stack depth limit** — A maximum of **200 undo entries** is imposed in `UndoRedoService`. The oldest entry is silently dropped when the limit is exceeded (see Step 2).
4. **Ctrl+Y vs Ctrl+Shift+Z** — **Both gestures** are bound to `RedoCommand` (confirmed).
5. **Folder settings** — `FolderSettingsViewModel` (folder-level auth/description) is **not in scope** (confirmed).
6. **Visual indicator** — **No toast or status message** will be shown on undo/redo at this time.
7. **Control-level undo stacks** — **Disabled globally.** `TextBox.UndoLimit=0` via a single application-level style in `App.axaml`; `SyntaxEditor.Document.UndoStack.SizeLimit=0` set in the constructor and on every `DocumentProperty` change. This ensures Ctrl+Z is never consumed by a child control and always reaches the Window-level `UndoCommand`. The accepted tradeoff is that edits within the 1.5-second debounce window are not undoable until the first snapshot commits (see Step 4a).
