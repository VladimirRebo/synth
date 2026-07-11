import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import SearchResultItem from './SearchResultItem.vue'
import type { SearchResult } from '../api'

// SYNTH-40: a result carrying a sourceUrl renders its path as an external link; without one it
// falls back to the plain path text exactly as before.
function result(overrides: Partial<SearchResult> = {}): SearchResult {
  return {
    relativePath: 'src/Greeter.cs',
    className: 'Greeter',
    methodName: 'Greet',
    qualifiedName: 'Sample.Greeter.Greet',
    chunkType: 'Method',
    startLine: 4,
    endLine: 6,
    snippet: 'public string Greet(string name) => $"Hello, {name}!";',
    score: 1.2,
    sourceUrl: null,
    collection: null,
    ...overrides,
  }
}

describe('SearchResultItem', () => {
  it('renders the path as an external link when sourceUrl is present', () => {
    const url = 'https://github.com/owner/repo/blob/main/src/Greeter.cs#L4-L6'
    const wrapper = mount(SearchResultItem, { props: { result: result({ sourceUrl: url }) } })

    const link = wrapper.get('a.path')
    expect(link.attributes('href')).toBe(url)
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toBe('noopener noreferrer')
    expect(link.text()).toBe('src/Greeter.cs')
  })

  it('renders the plain path (no link) when sourceUrl is absent', () => {
    const wrapper = mount(SearchResultItem, { props: { result: result({ sourceUrl: null }) } })

    expect(wrapper.find('a.path').exists()).toBe(false)
    expect(wrapper.get('span.path').text()).toBe('src/Greeter.cs')
  })

  // SYNTH-48: all-collections results carry a `collection`, shown as a badge so you can see which
  // repo each hit came from; single-collection results (collection null) omit it to avoid noise.
  it('shows the collection badge when the result carries a collection', () => {
    const wrapper = mount(SearchResultItem, { props: { result: result({ collection: 'my-repo' }) } })

    const badge = wrapper.get('.collection-badge')
    expect(badge.text()).toBe('my-repo')
  })

  it('omits the collection badge when the result has no collection', () => {
    const wrapper = mount(SearchResultItem, { props: { result: result({ collection: null }) } })

    expect(wrapper.find('.collection-badge').exists()).toBe(false)
  })
})
