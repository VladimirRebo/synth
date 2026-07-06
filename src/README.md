# src/

Application code goes here — this is where the loop builds Synth.

Planned layout (created incrementally by loop tasks, starting with `SYNTH-1`):

```
src/
  Synth.sln
  Synth.Api/          # ASP.NET Core Web API (.NET)
  Synth.Api.Tests/    # xUnit tests
  ...                  # Core / Infrastructure / Mcp projects later (see GitHub issues labeled `roadmap`)
  client/              # Vue 3 + Vite SPA (later)
```

Empty for now on purpose — `SYNTH-1` bootstraps the solution.
