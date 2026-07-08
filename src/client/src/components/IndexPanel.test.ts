import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import IndexPanel from './IndexPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedIndexSource = vi.mocked(api.indexSource)
const mockedListRepositories = vi.mocked(api.listRepositories)

beforeEach(() => {
  mockedIndexSource.mockReset()
  mockedListRepositories.mockReset()
  mockedListRepositories.mockResolvedValue([])
})

describe('IndexPanel', () => {
  it('calls indexSource with the entered path and shows the summary', async () => {
    mockedIndexSource.mockResolvedValue({ filesIndexed: 3, filesSkipped: 1, chunksIndexed: 12 })

    const wrapper = mount(IndexPanel)
    await wrapper.get('input[aria-label="Directory path"]').setValue('/repo/src')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).toHaveBeenCalledWith({ path: '/repo/src' })
    expect(wrapper.text()).toContain('Indexed 3 files')
    expect(wrapper.text()).toContain('12 chunks')
    expect(wrapper.text()).toContain('1 skipped')
    expect(wrapper.get('.status').text()).toBe('Done')
  })

  it('shows an error message when indexing fails', async () => {
    mockedIndexSource.mockRejectedValue(new Error('Directory not found: /nope'))

    const wrapper = mount(IndexPanel)
    await wrapper.get('input[aria-label="Directory path"]').setValue('/nope')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Directory not found: /nope')
    expect(wrapper.get('.status').text()).toBe('Error')
  })

  it('does not submit for a blank path', async () => {
    const wrapper = mount(IndexPanel)
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).not.toHaveBeenCalled()
  })

  it('switches to repository-URL mode and calls indexSource with repoUrl + branch', async () => {
    mockedIndexSource.mockResolvedValue({ filesIndexed: 5, filesSkipped: 0, chunksIndexed: 20 })

    const wrapper = mount(IndexPanel)
    await wrapper.get('[role="tab"]:nth-of-type(2)').trigger('click')
    await wrapper.get('input[aria-label="Repository URL"]').setValue('https://github.com/owner/repo')
    await wrapper.get('input[aria-label="Branch"]').setValue('main')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).toHaveBeenCalledWith({
      repoUrl: 'https://github.com/owner/repo',
      branch: 'main',
    })
    expect(wrapper.get('.status').text()).toBe('Done')
  })

  it('does not submit a blank repository URL', async () => {
    const wrapper = mount(IndexPanel)
    await wrapper.get('[role="tab"]:nth-of-type(2)').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).not.toHaveBeenCalled()
  })
})
