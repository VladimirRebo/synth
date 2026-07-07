import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SearchPanel from './SearchPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedSearch = vi.mocked(api.search)

const sampleResult: api.SearchResult = {
  relativePath: 'Greeter.cs',
  className: 'Greeter',
  methodName: 'Greet',
  qualifiedName: 'Sample.Greeter.Greet',
  chunkType: 'Method',
  startLine: 4,
  endLine: 4,
  snippet: 'public string Greet(string name) => $"Hello, {name}!";',
}

beforeEach(() => {
  mockedSearch.mockReset()
})

describe('SearchPanel', () => {
  it('calls search with the query and default limit, and renders results', async () => {
    mockedSearch.mockResolvedValue([sampleResult])

    const wrapper = mount(SearchPanel)
    await wrapper.get('input[type="text"]').setValue('greet')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedSearch).toHaveBeenCalledWith('greet', 10)
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
})
