# ADR-003: No AI features in the application

**Date:** 2026-03-13  
**Status:** Accepted

---

## Context

This project uses an AI-first *development workflow* — AI coding agents (GitHub
Copilot, etc.) are used to build the application. This could be confused with the
application itself having AI features.

## Decision

The application has **no AI or LLM features** of any kind. No OpenAI, Semantic
Kernel, Azure AI, or any other AI SDK is a dependency of any project in this
solution.

## Reasons

1. **Offline-first** — every AI feature requires a network call to an external
   service, breaking the offline-first product principle.
2. **No API keys** — users should be able to run the app with zero configuration.
   AI features inevitably require key management.
3. **Performance** — AI inference adds latency that conflicts with the "feels
   instant" product goal.
4. **Scope** — Callsmith is a developer tool for testing APIs. AI assistance does
   not belong in the core workflow.

## Consequences

- The app is fully functional without an internet connection.
- Users need no accounts, no API keys, and no subscriptions.
- The development workflow (using Copilot to write code) is completely separate
  from the application runtime — agents write code, they are not embedded in the
  shipped binary.
