import { ref, watchEffect } from 'vue'

// Which local editor to deep-link search results into. A single personal preference (SYNTH-49),
// so it lives in localStorage — no backend Settings round-trip. Module-level singleton, matching
// the useTheme/useRepositories pattern: the picker in SearchPanel and every SearchResultItem share
// one reactive value.
export type Editor = 'jetbrains' | 'vscode' | 'cursor'

const STORAGE_KEY = 'synth:editor'

// User-facing labels for the preference picker, in display order.
export const EDITOR_OPTIONS: { value: Editor; label: string }[] = [
  { value: 'jetbrains', label: 'JetBrains (Rider/IntelliJ)' },
  { value: 'vscode', label: 'VS Code' },
  { value: 'cursor', label: 'Cursor' },
]

function initialEditor(): Editor {
  const stored = localStorage.getItem(STORAGE_KEY)
  if (stored === 'jetbrains' || stored === 'vscode' || stored === 'cursor') return stored
  return 'vscode'
}

const editor = ref<Editor>(initialEditor())

watchEffect(() => {
  localStorage.setItem(STORAGE_KEY, editor.value)
})

// Last path segment of the repository root — a good-enough guess for JetBrains' `project` query
// param. IDEs still navigate correctly even when the guessed project name is slightly off. Handles
// both POSIX (`/`) and Windows (`\`) roots and trailing separators.
function projectName(root: string): string {
  const segments = root.replace(/[/\\]+$/, '').split(/[/\\]/)
  return segments[segments.length - 1] || root
}

// Join an absolute repository root with a result's relative path, preserving the root's separator
// style (backslashes for a Windows root, forward slashes otherwise) and collapsing the seam so we
// never emit a doubled or missing separator.
function absolutePath(root: string, relativePath: string): string {
  const isWindows = root.includes('\\') && !root.includes('/')
  const sep = isWindows ? '\\' : '/'
  const trimmedRoot = root.replace(/[/\\]+$/, '')
  const trimmedRel = relativePath.replace(/^[/\\]+/, '')
  return `${trimmedRoot}${sep}${trimmedRel}`
}

// Build the deep-link URI for `editor` opening `root`/`relativePath` at `line`. Pure — exported for
// unit testing each editor's URI shape independent of the reactive preference.
export function editorUri(
  target: Editor,
  root: string,
  relativePath: string,
  line: number,
): string {
  const path = absolutePath(root, relativePath)
  switch (target) {
    case 'jetbrains':
      return `jetbrains://rider/navigate/reference?project=${encodeURIComponent(
        projectName(root),
      )}&path=${path}&line=${line}`
    case 'vscode':
      return `vscode://file/${path}:${line}`
    case 'cursor':
      return `cursor://file/${path}:${line}`
  }
}

export function useEditorLink() {
  return {
    editor,
    // Build the URI for the currently-selected editor.
    buildUri: (root: string, relativePath: string, line: number) =>
      editorUri(editor.value, root, relativePath, line),
  }
}
