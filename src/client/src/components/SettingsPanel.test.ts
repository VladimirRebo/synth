import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SettingsPanel from './SettingsPanel.vue'
import * as api from '../api'

// Focused per-section behavior (loading, saving, the Ollama picker/pull flow, raw-JSON
// validation) lives in VcsSettingsSection.test.ts / EmbeddingSettingsSection.test.ts /
// RawSettingsSection.test.ts. This file only proves the shell composes the three sections and
// wires RawSettingsSection's `saved` event to refreshing the other two.
vi.mock('../api')

const mockedGetVcs = vi.mocked(api.getVcsSettings)
const mockedGetEmbedding = vi.mocked(api.getEmbeddingSettings)
const mockedGetRaw = vi.mocked(api.getRawSettings)
const mockedUpdateRaw = vi.mocked(api.updateRawSettings)
const mockedGetOllamaModels = vi.mocked(api.getOllamaModels)
const mockedGetOllamaPullStatus = vi.mocked(api.getOllamaPullStatus)

function vcs(overrides: Partial<api.VcsSettings> = {}): api.VcsSettings {
  return {
    workspaceRoot: null,
    pollIntervalMinutes: 5,
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
  mockedGetEmbedding.mockReset()
  mockedGetRaw.mockReset()
  mockedUpdateRaw.mockReset()
  mockedGetOllamaModels.mockReset()
  mockedGetOllamaPullStatus.mockReset()
  mockedGetVcs.mockResolvedValue(vcs())
  mockedGetEmbedding.mockResolvedValue(embedding())
  mockedGetRaw.mockResolvedValue('{}')
  mockedGetOllamaModels.mockResolvedValue([])
  // EmbeddingSettingsSection polls pull status once on mount regardless of provider.
  mockedGetOllamaPullStatus.mockResolvedValue({ state: 'Idle', model: '', status: '', error: null })
})

describe('SettingsPanel', () => {
  it('renders all three sections', async () => {
    const wrapper = mount(SettingsPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('Workspace root') // Vcs
    expect(wrapper.text()).toContain('Provider') // Embedding
    expect(wrapper.text()).toContain('Advanced: Raw JSON') // Raw
  })

  it('refreshes the Vcs and Embedding sections after a raw-document save', async () => {
    mockedUpdateRaw.mockResolvedValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    mockedGetVcs.mockResolvedValueOnce(vcs()).mockResolvedValueOnce(vcs({ workspaceRoot: '/tmp/new' }))

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await wrapper.get('.subsection-toggle').trigger('click')
    await wrapper.get('.raw-json').setValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    await wrapper.get('.section:last-of-type .save-row button').trigger('click')
    await flushPromises()

    expect(mockedGetVcs).toHaveBeenCalledTimes(2) // initial load + reload triggered by the raw save
    expect(mockedGetEmbedding).toHaveBeenCalledTimes(2)
    expect(wrapper.get('input[placeholder*="workspaces"]').element.value).toBe('/tmp/new')
  })
})
