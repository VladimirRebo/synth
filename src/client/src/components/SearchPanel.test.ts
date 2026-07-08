import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SearchPanel from './SearchPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedSearch = vi.mocked(api.search)
const mockedListRepositories = vi.mocked(api.listRepositories)

function result(overrides: Partial<api.SearchResult> = {}): api.SearchResult {
  return {
    relativePath: 'Greeter.cs',
    className: 'Greeter',
    methodName: 'Greet',
    qualifiedName: 'Sample.Greeter.Greet',
    chunkType: 'Method',
    startLine: 4,
    endLine: 4,
    snippet: 'public string Greet(string name) => $"Hello, {name}!";',
    score: 1.2,
    ...overrides,
  }
}

const sampleResult = result()

beforeEach(() => {
  mockedSearch.mockReset()
  mockedListRepositories.mockReset()
  mockedListRepositories.mockResolvedValue([])
  localStorage.clear()
  window.history.replaceState({}, '', '/')
})

describe('SearchPanel', () => {
  it('calls search with the query, default limit and an abort signal, and renders results', async () => {
    mockedSearch.mockResolvedValue([sampleResult])

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('greet')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedSearch).toHaveBeenCalledWith('greet', 10, undefined, expect.any(AbortSignal))
    expect(wrapper.text()).toContain('Greeter.cs')
    expect(wrapper.text()).toContain('Sample.Greeter.Greet')
  })

  it('shows an empty-state message when a search returns nothing', async () => {
    mockedSearch.mockResolvedValue([])

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('nothing here')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('No results')
  })

  it('shows an error message when the search fails', async () => {
    mockedSearch.mockRejectedValue(new Error('Search failed (500)'))

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('greet')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Search failed (500)')
  })

  it('does not search for a blank query', async () => {
    const wrapper = mount(SearchPanel)
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedSearch).not.toHaveBeenCalled()
  })

  it('saves a search to localStorage history and reapplies it on click', async () => {
    mockedSearch.mockResolvedValue([sampleResult])

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('greet')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const stored = JSON.parse(localStorage.getItem('synth:searchHistory') ?? '[]')
    expect(stored).toHaveLength(1)
    expect(stored[0].query).toBe('greet')

    mockedSearch.mockClear()
    await wrapper.get('[aria-label="Search history"]').trigger('click')
    await wrapper.get('.history-entry').trigger('click')
    await flushPromises()

    expect(mockedSearch).toHaveBeenCalledWith('greet', 10, undefined, expect.any(AbortSignal))
  })

  it('filters results by chunk type client-side', async () => {
    mockedSearch.mockResolvedValue([
      result({ chunkType: 'Method', relativePath: 'A.cs' }),
      result({ chunkType: 'Class', relativePath: 'B.cs' }),
    ])

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('anything')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('A.cs')
    expect(wrapper.text()).toContain('B.cs')

    await wrapper.get('select[aria-label="Filter by chunk type"]').setValue('Class')

    expect(wrapper.text()).not.toContain('A.cs')
    expect(wrapper.text()).toContain('B.cs')
  })

  it('auto-searches on mount when the URL has a q param', async () => {
    window.history.replaceState({}, '', '/?q=preloaded&limit=5')
    mockedSearch.mockResolvedValue([sampleResult])

    mount(SearchPanel)
    await flushPromises()

    expect(mockedSearch).toHaveBeenCalledWith('preloaded', 5, undefined, expect.any(AbortSignal))
  })
})
