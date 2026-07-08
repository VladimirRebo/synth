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
}

export interface IndexSummary {
  filesIndexed: number
  filesSkipped: number
  chunksIndexed: number
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

export async function indexSource(source: IndexSource): Promise<IndexSummary> {
  const response = await fetch('/api/index', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(source),
  })

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Indexing failed (${response.status})`))
  }

  return (await response.json()) as IndexSummary
}

export async function listRepositories(): Promise<RepositoryEntry[]> {
  const response = await fetch('/api/repositories')

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response, `Listing repositories failed (${response.status})`))
  }

  return (await response.json()) as RepositoryEntry[]
}
