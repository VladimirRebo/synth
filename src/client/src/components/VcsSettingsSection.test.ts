import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import VcsSettingsSection from './VcsSettingsSection.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetVcs = vi.mocked(api.getVcsSettings)
const mockedUpdateVcs = vi.mocked(api.updateVcsSettings)
const mockedPollNow = vi.mocked(api.pollRepositoriesNow)

function vcs(overrides: Partial<api.VcsSettings> = {}): api.VcsSettings {
  return {
    workspaceRoot: null,
    pollIntervalMinutes: 5,
    github: { tokenSet: false },
    gitlab: { tokenSet: false },
    ...overrides,
  }
}

beforeEach(() => {
  mockedGetVcs.mockReset()
  mockedUpdateVcs.mockReset()
  mockedPollNow.mockReset()
  mockedGetVcs.mockResolvedValue(vcs())
})

describe('VcsSettingsSection', () => {
  it('loads settings on mount', async () => {
    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    expect(mockedGetVcs).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Workspace root')
  })

  it('shows a load error instead of the form when the initial fetch fails', async () => {
    mockedGetVcs.mockRejectedValue(new Error('network down'))

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    expect(wrapper.text()).toContain('network down')
    expect(wrapper.text()).not.toContain('Workspace root')
  })

  it('sends only the changed workspace root, omitting untouched tokens', async () => {
    mockedUpdateVcs.mockResolvedValue(vcs({ workspaceRoot: '/tmp/work' }))

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.get('input[placeholder*="workspaces"]').setValue('/tmp/work')
    await wrapper.findAll('.save-row button')[0]!.trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ workspaceRoot: '/tmp/work' })
    expect(wrapper.text()).toContain('Saved')
  })

  it('sends a changed poll interval, omitting untouched fields', async () => {
    mockedUpdateVcs.mockResolvedValue(vcs({ pollIntervalMinutes: 15 }))

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.get('input[type="number"]').setValue(15)
    await wrapper.findAll('.save-row button')[0]!.trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ pollIntervalMinutes: 15 })
  })

  it('clearing a set token sends an empty string', async () => {
    mockedGetVcs.mockResolvedValue(vcs({ github: { tokenSet: true } }))
    mockedUpdateVcs.mockResolvedValue(vcs())

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.findAll('.clear-toggle input[type="checkbox"]')[0]!.setValue(true)
    await wrapper.findAll('.save-row button')[0]!.trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ github: { token: '' } })
  })

  it('shows the save error without crashing', async () => {
    mockedUpdateVcs.mockRejectedValue(new Error('the workspace root probe failed'))

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.get('input[placeholder*="workspaces"]').setValue('/tmp/bad')
    await wrapper.findAll('.save-row button')[0]!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('the workspace root probe failed')
  })

  it('checking for updates now reports how many repositories are reindexing', async () => {
    mockedPollNow.mockResolvedValue({ triggered: 2 })

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.findAll('.save-row button')[1]!.trigger('click') // "Check for updates now"
    await flushPromises()

    expect(mockedPollNow).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Reindexing 2 repositories.')
  })

  it('checking for updates now reports no changes found', async () => {
    mockedPollNow.mockResolvedValue({ triggered: 0 })

    const wrapper = mount(VcsSettingsSection)
    await flushPromises()

    await wrapper.findAll('.save-row button')[1]!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('No changes found.')
  })

  it('exposes reload() so a parent can refresh it after an out-of-band change', async () => {
    const wrapper = mount(VcsSettingsSection)
    await flushPromises()
    expect(mockedGetVcs).toHaveBeenCalledTimes(1)

    mockedGetVcs.mockResolvedValue(vcs({ workspaceRoot: '/tmp/reloaded' }))
    await (wrapper.vm as unknown as { reload: () => Promise<void> }).reload()
    await flushPromises()

    expect(mockedGetVcs).toHaveBeenCalledTimes(2)
    expect(wrapper.get('input[placeholder*="workspaces"]').element.value).toBe('/tmp/reloaded')
  })
})
