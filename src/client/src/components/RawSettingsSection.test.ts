import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import RawSettingsSection from './RawSettingsSection.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetRaw = vi.mocked(api.getRawSettings)
const mockedUpdateRaw = vi.mocked(api.updateRawSettings)

beforeEach(() => {
  mockedGetRaw.mockReset()
  mockedUpdateRaw.mockReset()
  mockedGetRaw.mockResolvedValue('{}')
})

describe('RawSettingsSection', () => {
  it('fetches the raw document eagerly on mount, before it is ever expanded', async () => {
    mount(RawSettingsSection)
    await flushPromises()

    expect(mockedGetRaw).toHaveBeenCalledTimes(1)
  })

  it('shows a load error when the initial fetch fails', async () => {
    mockedGetRaw.mockRejectedValue(new Error('network down'))

    const wrapper = mount(RawSettingsSection)
    await flushPromises()

    expect(wrapper.text()).toContain('network down')
  })

  it('pretty-prints the raw config once expanded', async () => {
    mockedGetRaw.mockResolvedValue('{"Vcs":{"WorkspaceRoot":"/tmp"}}')

    const wrapper = mount(RawSettingsSection)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    expect(wrapper.get('.raw-json').element.value).toContain('"WorkspaceRoot": "/tmp"')
  })

  it('rejects invalid JSON in the editor without calling the API', async () => {
    const wrapper = mount(RawSettingsSection)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('{ not valid json')
    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateRaw).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Invalid JSON')
  })

  it('rejects a non-object JSON document without calling the API', async () => {
    const wrapper = mount(RawSettingsSection)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('[1, 2, 3]')
    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateRaw).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('must be a JSON object')
  })

  it('saves valid raw JSON and emits saved so the parent can refresh other sections', async () => {
    mockedUpdateRaw.mockResolvedValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')

    const wrapper = mount(RawSettingsSection)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateRaw).toHaveBeenCalledWith('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    expect(wrapper.text()).toContain('Saved')
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })

  it('shows the save error without emitting saved', async () => {
    mockedUpdateRaw.mockRejectedValue(new Error('the document failed validation'))

    const wrapper = mount(RawSettingsSection)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('{"Vcs":{}}')
    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('the document failed validation')
    expect(wrapper.emitted('saved')).toBeUndefined()
  })
})
