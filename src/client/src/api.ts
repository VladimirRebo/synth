// Typed client for Synth.Api's REST endpoints. Calls go through the same-origin `/api/*`
// prefix (proxied to the real backend by vite.config.ts) so the browser never needs CORS.
// Field names mirror the backend's JSON exactly: Synth.Api.Mcp.CodeSearchResult for search,
// Synth.Core.IndexingSummary for indexing.

export interface SearchResult {
  relativePath: string
  className: string | null
  methodName: string | null
  qualifiedName: string
  chunkType: string
  startLine: number
  endLine: number
  snippet: string
  // Rerank score: vector similarity x chunk-type weight x keyword boost. NOT bounded to
  // [0, 1] (the weight/boost multipliers can push it above 1) — treat as a relative ranking
  // signal within one result set, not a percentage. See CodeSearchService.SearchAsync.
  score: number
  // Provider blob URL (GitHub/GitLab) pointing at this chunk's line range, when the collection was
  // indexed by remote repoUrl. null for local-path-indexed results — render the plain path then.
  sourceUrl: string | null
  // Which collection this hit came from. Populated only in all-collections mode (?collection=*),
  // where the whole point is showing "found in collection X"; null for a single-collection search.
  collection: string | null
}

// Mirrors Synth.Api.Search.FileChunkResult — one entry per chunk the store holds for a file,
// in ascending line order. Unlike SearchResult this carries the assembled `embeddingText` (the
// exact text fed to the embedding model) alongside the raw `content`, which is the whole point of
// browsing: seeing what was actually embedded, not just re-reading the source.
export interface FileChunk {
  chunkType: string
  className: string | null
  methodName: string | null
  qualifiedName: string
  startLine: number
  endLine: number
  content: string
  summary: string | null
  embeddingText: string
}

// Mirrors Synth.Core.Indexing.IndexJobStatus. SYNTH-31 made POST /index fire-and-forget — this is
// what GET /index/status returns, polled by the client instead of the old synchronous summary.
export interface IndexJobStatus {
  state: 'Idle' | 'Running' | 'Done' | 'Failed'
  collection: string
  source: string
  filesIndexed: number
  filesSkipped: number
  totalFiles: number | null
  chunksIndexed: number
  startedAt: string | null
  finishedAt: string | null
  error: string | null
}

export interface IndexStartResponse {
  collection: string
  status: string
}

// Mirrors Synth.Api.Vcs.RepositoryEntry — one entry per indexed collection (local directory or
// remote GitHub/GitLab repo), populated after each successful POST /index run.
export interface RepositoryEntry {
  collection: string
  sourceType: 'local' | 'github' | 'gitlab' | 'other'
  source: string
  branch: string | null
  lastIndexedAt: string
  chunkCount: number
}

export type IndexSource = { path: string } | { repoUrl: string; branch?: string }

// Mirrors Synth.Api.Vcs.VcsSettingsResponse. Tokens are never sent by the server — only
// whether one is currently set.
export interface VcsSettings {
  workspaceRoot: string | null
  pollIntervalMinutes: number
  github: { tokenSet: boolean }
  gitlab: { tokenSet: boolean }
}

// PUT patch shape: a field present-but-omitted-here is left unchanged server-side; an explicit
// empty string clears a token back to anonymous access. pollIntervalMinutes: 0 disables polling.
export interface VcsSettingsPatch {
  workspaceRoot?: string | null
  pollIntervalMinutes?: number
  github?: { token?: string }
  gitlab?: { token?: string }
}

// Mirrors Synth.Api.Embeddings.EmbeddingSettingsResponse.
export interface EmbeddingSettings {
  provider: 'Ollama' | 'OpenAI' | null
  ollama: { endpoint: string | null; model: string | null }
  openai: { apiKeySet: boolean; model: string | null }
}

export interface EmbeddingSettingsPatch {
  provider?: string | null
  ollama?: { endpoint?: string | null; model?: string | null }
  openai?: { apiKey?: string; model?: string | null }
}

// Mirrors Synth.Api.Embeddings.OllamaPullStatus — the single current-or-most-recent model pull,
// polled by the client instead of streaming (SYNTH-50), same fire-and-forget shape as IndexJobStatus.
export interface OllamaPullStatus {
  state: 'Idle' | 'Running' | 'Done' | 'Failed'
  model: string
  status: string
  error: string | null
}

// Mirrors Synth.Api.Logging.LogEntry.
export interface LogEntry {
  timestamp: string
  level: 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'
  message: string
  exception: string | null
}

export interface LogsQuery {
  level?: string
  since?: string
  search?: string
}

interface ApiError {
  error?: string
}

async function parseErrorMessage(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as ApiError
    return body.error ?? fallback
  } catch {
    return fallback
  }
}

export async function search(
  query: string,
  limit = 10,
  collection?: string,
  signal?: AbortSignal,
): Promise<SearchResult[]> {
  const params = new URLSearchParams({ q: query, limit: String(limit) })
  if (collection) params.set('collection', collection)
  const response = await fetch(`/api/search?${params.toString()}`, { signal })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Search failed (${response.status})`))
  }

  return (await response.json()) as SearchResult[]
}

// Starts an index run (202 Accepted, fire-and-forget) or throws — 400 for bad input, 409 when a
// job is already running (both carry `{ error }`, handled uniformly by parseErrorMessage).
export async function indexSource(source: IndexSource): Promise<IndexStartResponse> {
  const response = await fetch('/api/index', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(source),
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Indexing failed (${response.status})`))
  }

  return (await response.json()) as IndexStartResponse
}

export async function getIndexStatus(): Promise<IndexJobStatus> {
  const response = await fetch('/api/index/status')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading index status failed (${response.status})`))
  }

  return (await response.json()) as IndexJobStatus
}

// Fetches every chunk stored for `relativePath` in `collection`, in ascending line order. The
// backend returns 404 when the file has no chunks (unindexed / unknown collection / genuinely
// empty file); this surfaces that as an empty array so callers render a plain "no chunks" state
// instead of treating a not-indexed file as an error. The path is encoded segment-by-segment so
// nested paths keep their `/` separators (the endpoint's {*relativePath} catch-all).
export async function getFileChunks(collection: string, relativePath: string): Promise<FileChunk[]> {
  const encodedPath = relativePath
    .split('/')
    .map(encodeURIComponent)
    .join('/')
  const response = await fetch(
    `/api/repositories/${encodeURIComponent(collection)}/files/${encodedPath}`,
  )

  if (response.status === 404) return []

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading file chunks failed (${response.status})`))
  }

  return (await response.json()) as FileChunk[]
}

export async function listRepositories(): Promise<RepositoryEntry[]> {
  const response = await fetch('/api/repositories')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Listing repositories failed (${response.status})`))
  }

  return (await response.json()) as RepositoryEntry[]
}

export async function deleteRepository(collection: string): Promise<void> {
  const response = await fetch(`/api/repositories/${encodeURIComponent(collection)}`, { method: 'DELETE' })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Deleting repository failed (${response.status})`))
  }
}

export async function getVcsSettings(): Promise<VcsSettings> {
  const response = await fetch('/api/settings/vcs')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading VCS settings failed (${response.status})`))
  }

  return (await response.json()) as VcsSettings
}

export async function updateVcsSettings(patch: VcsSettingsPatch): Promise<VcsSettings> {
  const response = await fetch('/api/settings/vcs', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Saving VCS settings failed (${response.status})`))
  }

  return (await response.json()) as VcsSettings
}

export async function getEmbeddingSettings(): Promise<EmbeddingSettings> {
  const response = await fetch('/api/settings/embedding')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading embedding settings failed (${response.status})`))
  }

  return (await response.json()) as EmbeddingSettings
}

export async function updateEmbeddingSettings(patch: EmbeddingSettingsPatch): Promise<EmbeddingSettings> {
  const response = await fetch('/api/settings/embedding', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Saving embedding settings failed (${response.status})`))
  }

  return (await response.json()) as EmbeddingSettings
}

// Lists the models available on the live Ollama instance (GET .../ollama/models). The backend
// resolves the effective Ollama endpoint (Settings override or the Aspire connection) and proxies
// Ollama's own /api/tags, returning just the model names.
export async function getOllamaModels(): Promise<string[]> {
  const response = await fetch('/api/settings/embedding/ollama/models')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading Ollama models failed (${response.status})`))
  }

  return (await response.json()) as string[]
}

// Kicks off a fire-and-forget pull of `model` (202 Accepted) or throws — 400 for a blank model, 409
// when a pull is already running. Progress is observed via getOllamaPullStatus, not this response.
export async function pullOllamaModel(model: string): Promise<void> {
  const response = await fetch('/api/settings/embedding/ollama/pull', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ model }),
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Starting the model pull failed (${response.status})`))
  }
}

export async function getOllamaPullStatus(): Promise<OllamaPullStatus> {
  const response = await fetch('/api/settings/embedding/ollama/pull/status')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading pull status failed (${response.status})`))
  }

  return (await response.json()) as OllamaPullStatus
}

export async function getRawSettings(): Promise<string> {
  const response = await fetch('/api/settings/raw')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading raw config failed (${response.status})`))
  }

  return await response.text()
}

export async function updateRawSettings(json: string): Promise<string> {
  const response = await fetch('/api/settings/raw', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: json,
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Saving raw config failed (${response.status})`))
  }

  return await response.text()
}

export async function getLogs(query: LogsQuery = {}, signal?: AbortSignal): Promise<LogEntry[]> {
  const params = new URLSearchParams()
  if (query.level) params.set('level', query.level)
  if (query.since) params.set('since', query.since)
  if (query.search) params.set('search', query.search)
  const qs = params.toString()

  const response = await fetch(`/api/logs${qs ? `?${qs}` : ''}`, { signal })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Loading logs failed (${response.status})`))
  }

  return (await response.json()) as LogEntry[]
}
