import hljs from 'highlight.js/lib/core'
import csharp from 'highlight.js/lib/languages/csharp'
import DOMPurify from 'dompurify'

// Register languages explicitly (highlight.js/lib/core, not the full bundle with ~190
// languages) so the client stays small. Synth only chunks C# today (CSharpRoslynChunker) —
// add more `registerLanguage` calls here as more chunkers ship.
hljs.registerLanguage('csharp', csharp)

const extensionToLanguage: Record<string, string> = {
  cs: 'csharp',
}

/** Syntax-highlights `code` (guessing the language from `relativePath`'s extension when
 * known, falling back to hljs's auto-detection) and returns sanitized HTML safe for `v-html`. */
export function highlightCode(code: string, relativePath: string): string {
  const ext = relativePath.split('.').pop()?.toLowerCase() ?? ''
  const lang = extensionToLanguage[ext]

  const html =
    lang && hljs.getLanguage(lang)
      ? hljs.highlight(code, { language: lang }).value
      : hljs.highlightAuto(code).value

  return DOMPurify.sanitize(html, { ALLOWED_TAGS: ['span'], ALLOWED_ATTR: ['class'] })
}
