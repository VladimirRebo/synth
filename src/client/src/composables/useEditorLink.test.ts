import { describe, it, expect } from 'vitest'
import { editorUri } from './useEditorLink'

// SYNTH-49: each editor gets its own deep-link URI shape, built from the collection's absolute
// local root + the result's relative path + start line.
describe('editorUri', () => {
  const root = '/home/me/proj'
  const relativePath = 'src/Greeter.cs'
  const line = 4

  it('builds a JetBrains reference URI with the root dir name as project', () => {
    expect(editorUri('jetbrains', root, relativePath, line)).toBe(
      'jetbrains://rider/navigate/reference?project=proj&path=/home/me/proj/src/Greeter.cs&line=4',
    )
  })

  it('builds a VS Code file URI', () => {
    expect(editorUri('vscode', root, relativePath, line)).toBe(
      'vscode://file//home/me/proj/src/Greeter.cs:4',
    )
  })

  it('builds a Cursor file URI', () => {
    expect(editorUri('cursor', root, relativePath, line)).toBe(
      'cursor://file//home/me/proj/src/Greeter.cs:4',
    )
  })

  it('collapses the seam so a trailing-slash root does not double the separator', () => {
    expect(editorUri('vscode', '/home/me/proj/', relativePath, line)).toBe(
      'vscode://file//home/me/proj/src/Greeter.cs:4',
    )
  })

  it('preserves a Windows root separator style', () => {
    expect(editorUri('vscode', 'C:\\code\\proj', 'src\\Greeter.cs', 4)).toBe(
      'vscode://file/C:\\code\\proj\\src\\Greeter.cs:4',
    )
  })
})
