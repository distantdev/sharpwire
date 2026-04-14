# Sharpwire

Sharpwire is a desktop playground for building and running multi-agent workflows visually.

It is intentionally **not** a production platform. The goal is to make it easy (and fun) to experiment with agent behavior, tool wiring, orchestration patterns, and plugin-driven extension without heavy setup.

![Sharpwire screenshot](screenshot.png)

## Why this is useful

- Build agent systems quickly with a visual graph instead of stitching everything together by hand.
- Try ideas fast: tweak prompts, tools, and handoff paths, then run again.
- Watch agent behavior in one place (chat, graph state, and logs).
- Explore orchestration patterns (explicit wired flows + orchestrator-driven delegation).
- Use it as a safe sandbox for learning, prototyping, and debugging agent concepts.

## What Sharpwire does

- **Visual agent graph**: connect agents with default and return/feedback handoffs.
- **Chat-driven execution**: send a task and let the orchestrator route work.
- **Tool-enabled agents**: agents can call tools and coordinate through handoffs.
- **Plugin system**: load workspace plugins and expose plugin settings in-app.
- **Session/workspace state**: persist scenes, agent layouts, and local settings.
- **Windows packaging + updates**: Velopack-based installer/update flow.

## Self-extending by design

One of the most interesting parts of Sharpwire is that it can **self-extend**:

- You can add new tools and behaviors via plugins.
- Agents can use existing tools to create or update assets in the workspace.
- The orchestrator can incorporate new capabilities as they become available.

In short: Sharpwire can grow its own toolbox while you experiment, which makes it great for agentic prototyping and "what if?" workflows.

## Who this is for

- Developers curious about multi-agent systems.
- People experimenting with orchestration, tools, and prompt design.
- Anyone who wants a local, visual way to play with agents for fun.

If you need hardened reliability, strict security boundaries, or enterprise guarantees, this project is not aiming for that today.

## Quick start (local)

1. Clone the repo.
2. Build and run the app:

```powershell
dotnet build
dotnet run --project Sharpwire.csproj
```

3. Open **Settings** and configure provider API keys.
4. Create/load a workspace and start experimenting.

## Releases

- Windows release packaging uses Velopack.
- See `RELEASES.md` for release flow and update details.

---

Sharpwire is about rapid iteration, playful exploration, and learning by building agent systems hands-on.
