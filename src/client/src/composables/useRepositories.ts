import { ref } from 'vue'
import { listRepositories, type RepositoryEntry } from '../api'

// Module-level singleton: IndexPanel triggers `refresh()` after a successful index run,
// SearchPanel reads `repositories` to populate its collection picker — same
// cross-component-singleton pattern as useTheme/useSearchFocus, avoids prop/event drilling.
const repositories = ref<RepositoryEntry[]>([])
const loaded = ref(false)
const error = ref('')

export function useRepositories() {
  async function refresh() {
    try {
      const result = await listRepositories()
      repositories.value = Array.isArray(result) ? result : []
      error.value = ''
    } catch (err) {
      // Leave the previous `repositories` value in place (stale-but-known beats empty-and-wrong)
      // and surface the failure instead of swallowing it, so callers can show it to the user.
      error.value = err instanceof Error ? err.message : String(err)
    } finally {
      loaded.value = true
    }
  }

  return { repositories, loaded, error, refresh }
}
