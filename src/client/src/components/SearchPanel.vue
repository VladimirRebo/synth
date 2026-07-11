<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { search, type RepositoryEntry, type SearchResult } from '../api'
import { useSearchFocus } from '../composables/useSearchFocus'
import { useRepositories } from '../composables/useRepositories'
import { useEditorLink, EDITOR_OPTIONS } from '../composables/useEditorLink'
import SearchResultItem from './SearchResultItem.vue'
import Icon from './Icon.vue'

const { inputRef } = useSearchFocus()
const { repositories, refresh: refreshRepositories } = useRepositories()
const { editor } = useEditorLink()
const route = useRoute()
const router = useRouter()

// Sentinel the backend (GET /search) accepts in place of a real collection name to mean "search
// every known collection at once" (CollectionNames.All) — merged into one ranked list, each result
// tagged with the collection it came from. Default selection, since not remembering which repo has
// what is the common case for a personal multi-repo tool.
const ALL_COLLECTIONS = '*'

const query = ref('')
const limit = ref(10)
// Defaults to all-collections. The empty string selects GET /search's own fallback
// (CollectionNames.Default, "default" — the same collection local-path indexing uses, so it shows
// up in the picker too once something's been indexed); a real collection name scopes to that repo.
const collection = ref(ALL_COLLECTIONS)
const results = ref<SearchResult[]>([])
const loading = ref(false)
const error = ref('')
const hasSearched = ref(false)

let searchAbort: AbortController | null = null

// --- History (localStorage) -------------------------------------------------------------
interface HistoryEntry {
  query: string
  limit: number
  collection?: string
  timestamp: number
}

const HISTORY_KEY = 'synth:searchHistory'
const MAX_HISTORY = 20

function loadHistory(): HistoryEntry[] {
  try {
    const parsed = JSON.parse(localStorage.getItem(HISTORY_KEY) ?? '[]')
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

const history = ref<HistoryEntry[]>(loadHistory())
const showHistory = ref(false)
const historyRoot = ref<HTMLElement | null>(null)

function saveToHistory(q: string, lim: number, col: string) {
  const entries = history.value.filter((h) => h.query !== q)
  entries.unshift({ query: q, limit: lim, collection: col || undefined, timestamp: Date.now() })
  entries.length = Math.min(entries.length, MAX_HISTORY)
  history.value = entries
  localStorage.setItem(HISTORY_KEY, JSON.stringify(entries))
}

function applyHistory(entry: HistoryEntry) {
  query.value = entry.query
  limit.value = entry.limit
  collection.value = entry.collection ?? ''
  showHistory.value = false
  onSubmit()
}

function clearHistory() {
  history.value = []
  localStorage.removeItem(HISTORY_KEY)
}

function onDocumentClick(e: MouseEvent) {
  if (showHistory.value && historyRoot.value && !historyRoot.value.contains(e.target as Node)) {
    showHistory.value = false
  }
}

// --- URL deep-link sync ------------------------------------------------------------------
// Routed through vue-router's own query API (not raw window.location/history.replaceState):
// with hash-based routing (see router.ts) the app's query string lives inside the hash
// (#/search?q=...), which manual History API calls against window.location can't see.
function syncUrl(q: string, lim: number, col: string) {
  const nextQuery: Record<string, string> = {}
  if (q) nextQuery.q = q
  if (lim !== 10) nextQuery.limit = String(lim)
  if (col) nextQuery.collection = col
  router.replace({ query: nextQuery })
}

function loadFromUrl(): boolean {
  const q = typeof route.query.q === 'string' ? route.query.q : ''
  const lim = typeof route.query.limit === 'string' ? route.query.limit : ''
  const col = typeof route.query.collection === 'string' ? route.query.collection : ''
  if (lim) limit.value = parseInt(lim, 10) || 10
  if (col) collection.value = col
  if (q) {
    query.value = q
    return true
  }
  return false
}

// --- Search --------------------------------------------------------------------------------
async function onSubmit() {
  const q = query.value.trim()
  if (!q) return

  searchAbort?.abort()
  const abort = (searchAbort = new AbortController())

  loading.value = true
  error.value = ''
  filterType.value = ''
  filterFile.value = ''

  try {
    const found = await search(q, limit.value, collection.value || undefined, abort.signal)
    if (abort.signal.aborted) return
    results.value = found
    hasSearched.value = true
    saveToHistory(q, limit.value, collection.value)
    syncUrl(q, limit.value, collection.value)
  } catch (err) {
    if (abort.signal.aborted) return
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    if (!abort.signal.aborted) loading.value = false
  }
}

// --- Filters (client-side over the current result batch) ---------------------------------
const filterType = ref('')
const filterFile = ref('')

const chunkTypes = computed(() => [...new Set(results.value.map((r) => r.chunkType))].sort())

const filteredResults = computed(() => {
  let list = results.value
  if (filterType.value) list = list.filter((r) => r.chunkType === filterType.value)
  if (filterFile.value) {
    const needle = filterFile.value.toLowerCase()
    list = list.filter((r) => r.relativePath.toLowerCase().includes(needle))
  }
  return list
})

// --- Repository resolution (for editor deep-links) ---------------------------------------
// A result's owning collection is its own `collection` in all-collections mode, otherwise the
// picker's selection ('' being GET /search's "default" fallback). Match that against the loaded
// RepositoryEntry list so SearchResultItem can build a local-editor link from `source`/`sourceType`.
function repoForResult(result: SearchResult): RepositoryEntry | undefined {
  const name =
    result.collection ??
    (collection.value === ALL_COLLECTIONS ? undefined : collection.value || 'default')
  if (!name) return undefined
  return repositories.value.find((r) => r.collection === name)
}

// --- Score insights --------------------------------------------------------------------
// Synth's rerank score isn't bounded to [0, 1] like plain cosine similarity (chunk-type
// weight and keyword boost can push it above 1) — a fixed percentage histogram would be
// misleading. Instead, normalize min-max across THIS result batch: "relative relevance"
// within the current search, not an absolute similarity percentage.
const scoreStats = computed(() => {
  if (results.value.length === 0) return null
  const scores = [...results.value.map((r) => r.score)].sort((a, b) => a - b)
  const sum = scores.reduce((total, s) => total + s, 0)
  return {
    min: scores[0],
    max: scores[scores.length - 1],
    avg: sum / scores.length,
    median: scores[Math.floor(scores.length / 2)],
  }
})

const scoreBuckets = computed(() => {
  const stats = scoreStats.value
  if (!stats) return []
  const span = stats.max - stats.min || 1
  const buckets = [
    { label: '80–100%', from: 0.8, count: 0, color: 'var(--status-green)' },
    { label: '60–80%', from: 0.6, count: 0, color: 'var(--status-green)' },
    { label: '40–60%', from: 0.4, count: 0, color: 'var(--status-yellow)' },
    { label: '20–40%', from: 0.2, count: 0, color: 'var(--status-yellow)' },
    { label: '0–20%', from: 0, count: 0, color: 'var(--status-red)' },
  ]
  for (const r of results.value) {
    const normalized = (r.score - stats.min) / span
    const bucket = buckets.find((b) => normalized >= b.from) ?? buckets[buckets.length - 1]
    bucket.count++
  }
  const maxCount = Math.max(...buckets.map((b) => b.count), 1)
  return buckets.map((b) => ({ ...b, widthPct: (b.count / maxCount) * 100 }))
})

// --- Keyboard --------------------------------------------------------------------------
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && showHistory.value) showHistory.value = false
}

onMounted(() => {
  refreshRepositories()
  if (loadFromUrl()) onSubmit()
  document.addEventListener('click', onDocumentClick, true)
  document.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  document.removeEventListener('click', onDocumentClick, true)
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <section class="panel">
    <h2>Search</h2>
    <form class="search-form" @submit.prevent="onSubmit">
      <div class="search-input-wrap">
        <Icon name="search" :size="16" class="search-icon" />
        <input
          :ref="(el) => (inputRef = el as HTMLInputElement | null)"
          v-model="query"
          type="text"
          placeholder="Search the indexed codebase… (Ctrl/Cmd+K)"
          aria-label="Search query"
        />
      </div>
      <div ref="historyRoot" class="history-wrap">
        <button
          type="button"
          class="icon-button"
          aria-label="Search history"
          :disabled="history.length === 0"
          @click="showHistory = !showHistory"
        >
          <Icon name="clock" :size="16" />
        </button>
        <div v-if="showHistory" class="history-dropdown">
          <div class="history-dropdown-header">
            <span>Recent searches</span>
            <button type="button" class="link-button" @click="clearHistory">Clear all</button>
          </div>
          <button
            v-for="entry in history"
            :key="entry.timestamp"
            type="button"
            class="history-entry"
            @click="applyHistory(entry)"
          >
            {{ entry.query }}
          </button>
        </div>
      </div>
      <select v-model="collection" aria-label="Collection to search" class="collection">
        <option :value="ALL_COLLECTIONS">All collections</option>
        <option value="">Default</option>
        <option
          v-for="repo in repositories.filter((r) => r.collection !== 'default')"
          :key="repo.collection"
          :value="repo.collection"
        >
          {{ repo.collection }} ({{ repo.sourceType }})
        </option>
      </select>
      <select v-model="editor" aria-label="Editor for deep-links" class="editor">
        <option v-for="opt in EDITOR_OPTIONS" :key="opt.value" :value="opt.value">
          {{ opt.label }}
        </option>
      </select>
      <input
        v-model.number="limit"
        type="number"
        min="1"
        max="50"
        aria-label="Number of results"
        class="limit"
      />
      <button type="submit" :disabled="loading">
        {{ loading ? 'Searching…' : 'Search' }}
      </button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="hasSearched && !loading && results.length === 0" class="empty">
      No results. Try indexing a directory first, or a different query.
    </p>

    <template v-if="results.length > 0">
      <div class="insights">
        <div class="stats">
          <span>min <strong>{{ scoreStats?.min.toFixed(2) }}</strong></span>
          <span>avg <strong>{{ scoreStats?.avg.toFixed(2) }}</strong></span>
          <span>median <strong>{{ scoreStats?.median.toFixed(2) }}</strong></span>
          <span>max <strong>{{ scoreStats?.max.toFixed(2) }}</strong></span>
        </div>
        <div class="histogram">
          <div v-for="bucket in scoreBuckets" :key="bucket.label" class="histogram-row">
            <span class="histogram-label">{{ bucket.label }}</span>
            <div class="histogram-bar-track">
              <div
                class="histogram-bar"
                :style="{ width: `${bucket.widthPct}%`, background: bucket.color }"
              />
            </div>
            <span class="histogram-count">{{ bucket.count }}</span>
          </div>
        </div>
      </div>

      <div class="filters">
        <select v-model="filterType" aria-label="Filter by chunk type">
          <option value="">All types</option>
          <option v-for="type in chunkTypes" :key="type" :value="type">{{ type }}</option>
        </select>
        <input
          v-model="filterFile"
          type="text"
          placeholder="Filter by file path…"
          aria-label="Filter by file path"
        />
        <span class="filter-count">{{ filteredResults.length }} / {{ results.length }}</span>
      </div>
    </template>

    <ul v-if="filteredResults.length > 0" class="results">
      <li v-for="(result, index) in filteredResults" :key="`${result.relativePath}-${result.startLine}-${index}`">
        <SearchResultItem
          :result="result"
          :source-type="repoForResult(result)?.sourceType"
          :source="repoForResult(result)?.source"
        />
      </li>
    </ul>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
}

.search-form {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
  position: relative;
}

.search-input-wrap {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 0 10px;
  background: var(--bg);
}

.search-input-wrap input {
  border: none;
  padding: 8px 0;
  flex: 1;
}

.search-input-wrap input:focus {
  outline: none;
}

.search-icon {
  color: var(--text);
  flex-shrink: 0;
}

input,
button,
select {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text-h);
}

.limit {
  width: 72px;
}

.collection {
  max-width: 160px;
}

.editor {
  max-width: 140px;
}

button {
  cursor: pointer;
  border-color: var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.icon-button {
  padding: 8px;
  display: flex;
  align-items: center;
}

.history-wrap {
  position: relative;
}

.history-dropdown {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  z-index: 10;
  width: 280px;
  max-height: 320px;
  overflow-y: auto;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.2);
}

.history-dropdown-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 12px;
  font-size: 12px;
  color: var(--text);
  border-bottom: 1px solid var(--border);
}

.link-button {
  border: none;
  background: none;
  padding: 0;
  color: var(--accent);
  font-size: 12px;
}

.history-entry {
  display: block;
  width: 100%;
  text-align: left;
  border: none;
  border-radius: 0;
  background: none;
  color: var(--text-h);
  padding: 8px 12px;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.history-entry:hover {
  background: var(--accent-bg);
}

.error {
  color: var(--status-red);
}

.empty {
  color: var(--text);
}

.insights {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: flex-start;
  margin-bottom: 16px;
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
  font-size: 13px;
}

.stats {
  display: flex;
  gap: 16px;
  color: var(--text);
  min-width: 160px;
}

.stats strong {
  color: var(--text-h);
}

.histogram {
  flex: 1;
  min-width: 220px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.histogram-row {
  display: grid;
  grid-template-columns: 56px 1fr 24px;
  align-items: center;
  gap: 8px;
}

.histogram-label {
  font-family: var(--mono);
  font-size: 11px;
  color: var(--text);
}

.histogram-bar-track {
  background: var(--code-bg);
  border-radius: 3px;
  height: 8px;
  overflow: hidden;
}

.histogram-bar {
  height: 100%;
  border-radius: 3px;
  transition: width 0.2s;
}

.histogram-count {
  font-size: 11px;
  color: var(--text);
  text-align: right;
}

.filters {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.filters select,
.filters input {
  padding: 6px 10px;
  font-size: 13px;
}

.filter-count {
  font-size: 12px;
  color: var(--text);
  margin-left: auto;
}

.results {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
</style>
