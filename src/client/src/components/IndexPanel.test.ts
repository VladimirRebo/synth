import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import IndexPanel from './IndexPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedIndexDirectory = vi.mocked(api.indexDirectory)

beforeEach(() => {
  mockedIndexDirectory.mockReset()
})

describe('IndexPanel', () => {
  it('calls indexDirectory with the entered path and shows the summary', async () => {
    mockedIndexDirectory.mockResolvedValue({ filesIndexed: 3, filesSkipped: 1, chunksIndexed: 12 })

    const wrapper = mount(IndexPanel)
    await wrapper.get('input').setValue('/repo/src')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexDirectory).toHaveBeenCalledWith('/repo/src')
    expect(wrapper.text()).toContain('Indexed 3 files')
    expect(wrapper.text()).toContain('12 chunks')
    expect(wrapper.text()).toContain('1 skipped')
    expect(wrapper.get('.status').text()).toBe('Done')
  })

  it('shows an error message when indexing fails', async () => {
    mockedIndexDirectory.mockRejectedValue(new Error('Directory not found: /nope'))

    const wrapper = mount(IndexPanel)
    await wrapper.get('input').setValue('/nope')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Directory not found: /nope')
    expect(wrapper.get('.status').text()).toBe('Error')
  })

  it('does not submit for a blank path', async () => {
    const wrapper = mount(IndexPanel)
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexDirectory).not.toHaveBeenCalled()
  })
})
