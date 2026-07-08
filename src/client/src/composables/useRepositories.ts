import { ref } from 'vue'
import { listRepositories, type RepositoryEntry } from '../api'

// Module-level singleton: IndexPanel triggers `refresh()` after a successful index run,
// SearchPanel reads `repositories` to populate its collection picker — same
// cross-component-singleton pattern as useTheme/useSearchFocus, avoids prop/event drilling.
const repositories = ref<RepositoryEntry[]>([])
const loaded = ref(false)

export function useRepositories() {
  async function refresh() {
    try {
      const result = await listRepositories()
      repositories.value = Array.isArray(result) ? result : []
    } finally {
      loaded.value = true
    }
  }

  return { repositories, loaded, refresh }
}
