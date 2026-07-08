import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SettingsPanel from './SettingsPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetVcs = vi.mocked(api.getVcsSettings)
const mockedUpdateVcs = vi.mocked(api.updateVcsSettings)
const mockedGetEmbedding = vi.mocked(api.getEmbeddingSettings)
const mockedUpdateEmbedding = vi.mocked(api.updateEmbeddingSettings)

function vcs(overrides: Partial<api.VcsSettings> = {}): api.VcsSettings {
  return {
    workspaceRoot: null,
    github: { tokenSet: false },
    gitlab: { tokenSet: false },
    ...overrides,
  }
}

function embedding(overrides: Partial<api.EmbeddingSettings> = {}): api.EmbeddingSettings {
  return {
    provider: null,
    ollama: { endpoint: null, model: null },
    openai: { apiKeySet: false, model: null },
    ...overrides,
  }
}

beforeEach(() => {
  mockedGetVcs.mockReset()
  mockedUpdateVcs.mockReset()
  mockedGetEmbedding.mockReset()
  mockedUpdateEmbedding.mockReset()
  mockedGetVcs.mockResolvedValue(vcs())
  mockedGetEmbedding.mockResolvedValue(embedding())
})

describe('SettingsPanel', () => {
  it('does not load settings until expanded', () => {
    mount(SettingsPanel)
    expect(mockedGetVcs).not.toHaveBeenCalled()
    expect(mockedGetEmbedding).not.toHaveBeenCalled()
  })

  it('loads both settings sections on first expand', async () => {
    const wrapper = mount(SettingsPanel)
    await wrapper.get('.panel-toggle').trigger('click')
    await flushPromises()

    expect(mockedGetVcs).toHaveBeenCalledTimes(1)
    expect(mockedGetEmbedding).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Workspace root')
    expect(wrapper.text()).toContain('Provider')
  })

  it('sends only the changed workspace root, omitting untouched tokens', async () => {
    mockedUpdateVcs.mockResolvedValue(vcs({ workspaceRoot: '/tmp/work' }))

    const wrapper = mount(SettingsPanel)
    await wrapper.get('.panel-toggle').trigger('click')
    await flushPromises()

    await wrapper.get('input[placeholder*="workspaces"]').setValue('/tmp/work')
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[0].trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ workspaceRoot: '/tmp/work' })
  })

  it('clearing a set token sends an empty string', async () => {
    mockedGetVcs.mockResolvedValue(vcs({ github: { tokenSet: true } }))
    mockedUpdateVcs.mockResolvedValue(vcs())

    const wrapper = mount(SettingsPanel)
    await wrapper.get('.panel-toggle').trigger('click')
    await flushPromises()

    const clearCheckboxes = wrapper.findAll('.clear-toggle input[type="checkbox"]')
    await clearCheckboxes[0].setValue(true)
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[0].trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ github: { token: '' } })
  })

  it('switches embedding provider to OpenAI and saves the model + key', async () => {
    mockedUpdateEmbedding.mockResolvedValue(
      embedding({ provider: 'OpenAI', openai: { apiKeySet: true, model: 'text-embedding-3-small' } }),
    )

    const wrapper = mount(SettingsPanel)
    await wrapper.get('.panel-toggle').trigger('click')
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('OpenAI')
    const passwordInputs = wrapper.findAll('input[type="password"]')
    await passwordInputs[passwordInputs.length - 1].setValue('sk-test')
    await wrapper.get('input[placeholder="text-embedding-3-small"]').setValue('text-embedding-3-small')

    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[1].trigger('click')
    await flushPromises()

    expect(mockedUpdateEmbedding).toHaveBeenCalledWith({
      provider: 'OpenAI',
      openai: { model: 'text-embedding-3-small', apiKey: 'sk-test' },
    })
    expect(wrapper.text()).toContain('Saved')
  })

  it('shows the probe-failure error without clearing the form', async () => {
    mockedUpdateEmbedding.mockRejectedValue(new Error('the embedding probe failed: connection refused'))

    const wrapper = mount(SettingsPanel)
    await wrapper.get('.panel-toggle').trigger('click')
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('Ollama')
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[1].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('the embedding probe failed: connection refused')
  })
})
