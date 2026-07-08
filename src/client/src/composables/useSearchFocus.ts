import { ref } from 'vue'

// Module-level singleton: SearchPanel binds its query input to `inputRef`, App.vue calls
// `focus()` from a single global Cmd/Ctrl+K listener. Avoids prop/event drilling for a
// cross-component shortcut.
const inputRef = ref<HTMLInputElement | null>(null)

export function useSearchFocus() {
  return {
    inputRef,
    focus: () => inputRef.value?.focus(),
  }
}
