import { describe, it, expect, vi } from 'vitest'
import { useSearchFocus } from './useSearchFocus'

describe('useSearchFocus', () => {
  it('focus() calls .focus() on the bound input element', () => {
    const { inputRef, focus } = useSearchFocus()
    const input = document.createElement('input')
    const focusSpy = vi.spyOn(input, 'focus')
    inputRef.value = input

    focus()

    expect(focusSpy).toHaveBeenCalledTimes(1)
  })

  it('focus() is a no-op when no input is bound', () => {
    const { inputRef, focus } = useSearchFocus()
    inputRef.value = null

    expect(() => focus()).not.toThrow()
  })

  it('is a module-level singleton: bindings made through one call are visible through another', () => {
    const a = useSearchFocus()
    const b = useSearchFocus()
    const input = document.createElement('input')

    a.inputRef.value = input

    expect(b.inputRef.value).toBe(input)
  })
})
