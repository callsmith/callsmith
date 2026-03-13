# ADR-001: Avalonia UI over WPF / MAUI / Electron

**Date:** 2026-03-13  
**Status:** Accepted

---

## Context

A cross-platform native desktop UI framework is required. The candidates evaluated were:

- **WPF** — Windows-only, ruled out immediately.
- **.NET MAUI** — targets mobile primarily; desktop support is immature and the
  tooling experience on Windows/macOS is still rough.
- **Electron / Tauri** — not native .NET; Electron ships a full Chromium runtime
  (~150 MB overhead), making it incompatible with the "fast, native" product goal.
- **Avalonia UI** — true cross-platform native .NET UI framework. Renders via Skia
  directly on each platform (no OS widget proxying). Familiar XAML/MVVM model.
  Excellent desktop-class control library. Active community and commercial support.

## Decision

Use **Avalonia UI** as the sole UI framework.

## Consequences

- The app renders consistently across Windows, macOS, and Linux.
- The MVVM pattern (views + view models + data binding) is idiomatic and familiar
  to anyone with WPF or UWP experience.
- Avalonia-specific AXAML syntax is used instead of standard XAML, but the
  differences are minor and well-documented.
- No web runtime dependency — the published binary is self-contained and small.
