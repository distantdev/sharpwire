# Agentic Playground: Coding Standards & Best Practices

**Stack:** .NET 10.0 · Microsoft Agent Framework 1.0 · Avalonia 11.x · Nodify

---

## 1. Architectural foundations

### 1.1 Hybrid orchestration model

The system follows a **dual-path** communication strategy:

- **Wired (explicit)** — When a user draws a connection in Nodify, it represents a transition in a MAF `StateMap`. Data flows as a full context handoff from the output of node A to the input of node B.
- **Orchestrated (implicit)** — A central manager agent (*Magentic*) has a global view of all available agents. It can invoke any agent in the pool as a tool.
- **Chain entry** — If an orchestrator targets a wired sequence, it must target the node flagged with `IsChainEntry: true`.

### 1.2 Data flow and state

| Concern | Rule |
|--------|------|
| **Source of truth** | The `AgentSession` and its associated `IStateStore` own authoritative data. |
| **View consistency** | Nodify view models are reactive projections. They request changes to the workflow builder; they do not hold the definitive state of the agentic graph. |
| **Immutability** | Treat message history as immutable. Use MAF’s built-in message collection patterns rather than manual string concatenation. |

---

## 2. Agent and tool definitions (YAML)

For portability and editing in the playground, **agents and tools must be defined in YAML** using the MAF 1.0 declarative schema.

### 2.1 Agent schema

```yaml
kind: Agent
metadata:
  name: "CodeReviewer"
  is_chain_entry: true  # Flag for orchestrator entry points
spec:
  model: "gpt-4o"
  instructions: |
    You are a senior .NET developer.
    Review the incoming code for thread-safety and Avalonia best practices.
  tools:
    - name: "FileReadTool"
  parameters:
    temperature: 0.2
```

### 2.2 Tool registration

- **Discovery** — Tools are C# classes implementing `IStreamingTool`, registered via the YAML `tools` section.
- **Late binding** — The UI resolves tool names to local C# implementations when the workflow is instantiated.

---

## 3. Security and sandboxing

Until full containerization exists, these **“renter-friendly”** guardrails are mandatory.

### 3.1 Workspace isolation

- **Root pinning** — All filesystem tools must accept a `RootPath` via dependency injection. Any path resolution that escapes the workspace (e.g. `..`) must throw `SecurityException`.
- **Process safety** — Avoid `Process.Start` or shell execution unless the command is hard-coded in the tool’s C# implementation. **Never** pass raw LLM output directly to a shell.

### 3.2 Secret management

- **Zero-key policy** — Do not store API keys in YAML or `appsettings.json`.
- **Provider** — Use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) (`dotnet user-secrets`) for local development.
- **Redaction** — Implement `MessageMiddleware` in the agent pipeline to mask strings matching common API key patterns (e.g. `sk-...`) before logging to the UI.

---

## 4. UI and threading (Avalonia + Nodify)

### 4.1 Threading boundaries

- The agent framework is **async-first**.
- **Offloading** — Run `workflow.RunAsync()` (and similar) on a background context; do not block the UI thread.
- **Dispatcher** — Use `Dispatcher.UIThread.Post` or `InvokeAsync` only for:
  - Updating node positions
  - Appending a finished message to chat
  - Showing human-in-the-loop (HITL) dialogs

### 4.2 Performance in Nodify

- **Compiled bindings** — Set `x:DataType` on all node `DataTemplate`s.
- **Throttling** — When streaming tokens, do not update the UI per character. Batch updates (e.g. every 50–100 ms) so the render loop stays responsive.

---

## 5. Human-in-the-loop (HITL)

Standardize approvals so high-impact tools cannot run unchecked.

- **Interruption** — Use the MAF **interrupt** capability where appropriate.
- **Requirement** — Any tool marked **impact: high** (e.g. file delete, HTTP request) must emit a `RequestApprovalEvent` (or equivalent approval flow).
- **State persistence** — The playground must be able to serialize the current `AgentSession` so the user can close the app and resume a **pending approval** later.

---

## 6. Coding style and quality

- **Nullable reference types** — Enabled project-wide.
- **Async/await** — Avoid `Task.Wait()`, `.Result`, and `.GetAwaiter().GetResult()` on UI-bound code paths (prevents deadlocks).
- **Dependency injection** — Use `Microsoft.Extensions.DependencyInjection` for services (loggers, state stores, tool registries, etc.).

---

## 7. Release and update standards

### 7.1 Packaging baseline

- **Installer system** — Use Velopack (`vpk`) for Windows packaging and updates.
- **Bootstrap requirement** — `Program.Main` must call `VelopackApp.Build().Run()` before Avalonia startup.
- **Installer icon** — Packaging must pass `Assets/icon.ico` to `vpk pack --icon` (script default: `scripts/build-velopack.ps1`).

### 7.2 Update source and UX

- **Single source** — App updates are hardcoded to `https://github.com/distantdev/sharpwire`.
- **No repo fields in Settings** — Update repository owner/name is not user-configurable in UI.
- **Settings controls** — Keep Updates section limited to:
  - `Check for updates`
  - `Install updates`
  - `Auto check and install updates`

### 7.3 Build outputs and versioning

- **Generated output** — `artifacts/` is build output and must remain gitignored.
- **Release trigger** — Use semantic tags (`vX.Y.Z`) to drive release automation and published installer assets.
