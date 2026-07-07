<script setup lang="ts">
import { ref } from 'vue'
import { search, type SearchResult } from '../api'
import SearchResultItem from './SearchResultItem.vue'

const query = ref('')
const limit = ref(10)
const results = ref<SearchResult[]>([])
const loading = ref(false)
const error = ref('')
const hasSearched = ref(false)

async function onSubmit() {
  if (!query.value.trim()) return

  loading.value = true
  error.value = ''
  try {
    results.value = await search(query.value, limit.value)
    hasSearched.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <section class="panel">
    <h2>Search</h2>
    <form class="search-form" @submit.prevent="onSubmit">
      <input
        v-model="query"
        type="text"
        placeholder="Search the indexed codebase…"
        aria-label="Search query"
      />
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

    <ul v-if="results.length > 0" class="results">
      <li v-for="(result, index) in results" :key="`${result.relativePath}-${result.startLine}-${index}`">
        <SearchResultItem :result="result" />
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
}

.search-form input[type='text'] {
  flex: 1;
}

input,
button {
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

.error {
  color: #e5484d;
}

.empty {
  color: var(--text);
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
