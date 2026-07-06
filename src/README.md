# src/

Application code goes here — this is where the loop builds Walter.

Planned layout (created incrementally by loop tasks, starting with `WALTER-1`):

```
src/
  Walter.sln
  Walter.Api/          # ASP.NET Core Web API (.NET)
  Walter.Api.Tests/    # xUnit tests
  ...                  # Core / Infrastructure / Mcp projects later (see docs/ROADMAP.md)
  client/              # Vue 3 + Vite SPA (later)
```

Empty for now on purpose — `WALTER-1` bootstraps the solution.
