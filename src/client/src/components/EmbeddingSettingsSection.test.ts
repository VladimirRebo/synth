import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import EmbeddingSettingsSection from './EmbeddingSettingsSection.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetEmbedding = vi.mocked(api.getEmbeddingSettings)
const mockedUpdateEmbedding = vi.mocked(api.updateEmbeddingSettings)
const mockedGetOllamaModels = vi.mocked(api.getOllamaModels)
const mockedPullOllamaModel = vi.mocked(api.pullOllamaModel)
const mockedGetOllamaPullStatus = vi.mocked(api.getOllamaPullStatus)

function pullStatus(overrides: Partial<api.OllamaPullStatus> = {}): api.OllamaPullStatus {
  return { state: 'Idle', model: '', status: '', error: null, ...overrides }
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
  mockedGetEmbedding.mockReset()
  mockedUpdateEmbedding.mockReset()
  mockedGetOllamaModels.mockReset()
  mockedPullOllamaModel.mockReset()
  mockedGetOllamaPullStatus.mockReset()
  mockedGetEmbedding.mockResolvedValue(embedding())
  mockedGetOllamaModels.mockResolvedValue([])
  mockedGetOllamaPullStatus.mockResolvedValue(pullStatus()) // idle by default (mount's own poll)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('EmbeddingSettingsSection', () => {
  it('loads settings on mount', async () => {
    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    expect(mockedGetEmbedding).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Provider')
  })

  it('shows a load error instead of the form when the initial fetch fails', async () => {
    mockedGetEmbedding.mockRejectedValue(new Error('network down'))

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    expect(wrapper.text()).toContain('network down')
    expect(wrapper.text()).not.toContain('Provider')
  })

  it('switches embedding provider to OpenAI and saves the model + key', async () => {
    mockedUpdateEmbedding.mockResolvedValue(
      embedding({ provider: 'OpenAI', openai: { apiKeySet: true, model: 'text-embedding-3-small' } }),
    )

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('OpenAI')
    await wrapper.get('input[type="password"]').setValue('sk-test')
    await wrapper.get('input[placeholder="text-embedding-3-small"]').setValue('text-embedding-3-small')

    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateEmbedding).toHaveBeenCalledWith({
      provider: 'OpenAI',
      openai: { model: 'text-embedding-3-small', apiKey: 'sk-test' },
    })
    expect(wrapper.text()).toContain('Saved')
  })

  it('shows the probe-failure error without clearing the form', async () => {
    mockedUpdateEmbedding.mockRejectedValue(new Error('the embedding probe failed: connection refused'))

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('Ollama')
    await wrapper.get('.save-row button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('the embedding probe failed: connection refused')
  })

  it('renders the Ollama model picker with models fetched from the backend', async () => {
    mockedGetEmbedding.mockResolvedValue(embedding({ provider: 'Ollama' }))
    mockedGetOllamaModels.mockResolvedValue(['nomic-embed-text:latest', 'llama3:8b'])

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    expect(mockedGetOllamaModels).toHaveBeenCalled()
    const select = wrapper.get('select[aria-label="Ollama model"]')
    const options = select.findAll('option').map((o) => o.text())
    expect(options).toContain('nomic-embed-text:latest')
    expect(options).toContain('llama3:8b')
  })

  it('fetches the model list when switching the provider to Ollama', async () => {
    mockedGetOllamaModels.mockResolvedValue(['nomic-embed-text'])

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()
    expect(mockedGetOllamaModels).not.toHaveBeenCalled() // default provider: no fetch

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('Ollama')
    await flushPromises()

    expect(mockedGetOllamaModels).toHaveBeenCalledTimes(1)
    expect(wrapper.get('select[aria-label="Ollama model"]').text()).toContain('nomic-embed-text')
  })

  it('triggers a pull and polls status to completion, then refreshes the model list', async () => {
    vi.useFakeTimers()
    mockedGetEmbedding.mockResolvedValue(embedding({ provider: 'Ollama' }))
    mockedGetOllamaModels
      .mockResolvedValueOnce([]) // mount fetch: nothing yet
      .mockResolvedValue(['nomic-embed-text']) // refreshed after the pull completes
    mockedPullOllamaModel.mockResolvedValue(undefined)
    mockedGetOllamaPullStatus
      .mockResolvedValueOnce(pullStatus()) // mount poll: idle
      .mockResolvedValueOnce(pullStatus({ state: 'Running', model: 'nomic-embed-text', status: 'pulling manifest' })) // right after POST
      .mockResolvedValueOnce(pullStatus({ state: 'Running', model: 'nomic-embed-text', status: 'downloading (50%)' }))
      .mockResolvedValueOnce(pullStatus({ state: 'Done', model: 'nomic-embed-text' }))

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    await wrapper.get('input[aria-label="Model to pull"]').setValue('nomic-embed-text')
    await wrapper.get('.pull-button').trigger('click')
    await flushPromises()

    expect(mockedPullOllamaModel).toHaveBeenCalledWith('nomic-embed-text')
    expect(wrapper.text()).toContain('Pulling nomic-embed-text… pulling manifest')

    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.text()).toContain('Pulling nomic-embed-text… downloading (50%)')

    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.text()).toContain('Pulled nomic-embed-text.')

    // Refreshed after completion — the newly pulled model is now an option.
    await flushPromises()
    expect(wrapper.get('select[aria-label="Ollama model"]').text()).toContain('nomic-embed-text')

    // Polling stops once the pull is no longer Running.
    mockedGetOllamaPullStatus.mockClear()
    await vi.advanceTimersByTimeAsync(3000)
    expect(mockedGetOllamaPullStatus).not.toHaveBeenCalled()
  })

  it('shows a 409 error when a pull is already running, without crashing', async () => {
    mockedGetEmbedding.mockResolvedValue(embedding({ provider: 'Ollama' }))
    mockedPullOllamaModel.mockRejectedValue(new Error('A model pull is already running.'))

    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()

    await wrapper.get('input[aria-label="Model to pull"]').setValue('another')
    await wrapper.get('.pull-button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('A model pull is already running.')
  })

  it('exposes reload() so a parent can refresh it after an out-of-band change', async () => {
    const wrapper = mount(EmbeddingSettingsSection)
    await flushPromises()
    expect(mockedGetEmbedding).toHaveBeenCalledTimes(1)

    mockedGetEmbedding.mockResolvedValue(embedding({ provider: 'OpenAI', openai: { apiKeySet: false, model: 'new-model' } }))
    await (wrapper.vm as unknown as { reload: () => Promise<void> }).reload()
    await flushPromises()

    expect(mockedGetEmbedding).toHaveBeenCalledTimes(2)
    expect(wrapper.get('select[aria-label="Embedding provider"]').element.value).toBe('OpenAI')
  })
})
