import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SettingsPanel from './SettingsPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetVcs = vi.mocked(api.getVcsSettings)
const mockedUpdateVcs = vi.mocked(api.updateVcsSettings)
const mockedPollNow = vi.mocked(api.pollRepositoriesNow)
const mockedGetEmbedding = vi.mocked(api.getEmbeddingSettings)
const mockedUpdateEmbedding = vi.mocked(api.updateEmbeddingSettings)
const mockedGetOllamaModels = vi.mocked(api.getOllamaModels)
const mockedPullOllamaModel = vi.mocked(api.pullOllamaModel)
const mockedGetOllamaPullStatus = vi.mocked(api.getOllamaPullStatus)
const mockedGetRaw = vi.mocked(api.getRawSettings)
const mockedUpdateRaw = vi.mocked(api.updateRawSettings)

// Index-based `.save-row button` lookups are fragile (a new button anywhere before the target shifts
// every later index) — used only where an existing test already relied on it; new tests find by text.
function findButtonByText(wrapper: ReturnType<typeof mount>, text: string) {
  const button = wrapper.findAll('.save-row button').find((b) => b.text() === text)
  if (!button) throw new Error(`No .save-row button with text "${text}"`)
  return button
}

function pullStatus(overrides: Partial<api.OllamaPullStatus> = {}): api.OllamaPullStatus {
  return { state: 'Idle', model: '', status: '', error: null, ...overrides }
}

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
  mockedUpdateVcs.mockReset()
  mockedPollNow.mockReset()
  mockedGetEmbedding.mockReset()
  mockedUpdateEmbedding.mockReset()
  mockedGetOllamaModels.mockReset()
  mockedPullOllamaModel.mockReset()
  mockedGetOllamaPullStatus.mockReset()
  mockedGetRaw.mockReset()
  mockedUpdateRaw.mockReset()
  mockedGetVcs.mockResolvedValue(vcs())
  mockedGetEmbedding.mockResolvedValue(embedding())
  mockedGetOllamaModels.mockResolvedValue([])
  mockedGetOllamaPullStatus.mockResolvedValue(pullStatus()) // idle by default (mount's own poll)
  mockedGetRaw.mockResolvedValue('{}')
})

afterEach(() => {
  vi.useRealTimers()
})

describe('SettingsPanel', () => {
  it('loads both settings sections on mount', async () => {
    const wrapper = mount(SettingsPanel)
    await flushPromises()

    expect(mockedGetVcs).toHaveBeenCalledTimes(1)
    expect(mockedGetEmbedding).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Workspace root')
    expect(wrapper.text()).toContain('Provider')
  })

  it('sends only the changed workspace root, omitting untouched tokens', async () => {
    mockedUpdateVcs.mockResolvedValue(vcs({ workspaceRoot: '/tmp/work' }))

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await wrapper.get('input[placeholder*="workspaces"]').setValue('/tmp/work')
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[0].trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ workspaceRoot: '/tmp/work' })
  })

  it('sends a changed poll interval, omitting untouched fields', async () => {
    mockedUpdateVcs.mockResolvedValue(vcs({ pollIntervalMinutes: 15 }))

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await wrapper.get('input[type="number"]').setValue(15)
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[0].trigger('click')
    await flushPromises()

    expect(mockedUpdateVcs).toHaveBeenCalledWith({ pollIntervalMinutes: 15 })
  })

  it('checking for updates now reports how many repositories are reindexing', async () => {
    mockedPollNow.mockResolvedValue({ triggered: 2 })

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await findButtonByText(wrapper, 'Check for updates now').trigger('click')
    await flushPromises()

    expect(mockedPollNow).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Reindexing 2 repositories.')
  })

  it('checking for updates now reports no changes found', async () => {
    mockedPollNow.mockResolvedValue({ triggered: 0 })

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await findButtonByText(wrapper, 'Check for updates now').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('No changes found.')
  })

  it('clearing a set token sends an empty string', async () => {
    mockedGetVcs.mockResolvedValue(vcs({ github: { tokenSet: true } }))
    mockedUpdateVcs.mockResolvedValue(vcs())

    const wrapper = mount(SettingsPanel)
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
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('OpenAI')
    const passwordInputs = wrapper.findAll('input[type="password"]')
    await passwordInputs[passwordInputs.length - 1].setValue('sk-test')
    await wrapper.get('input[placeholder="text-embedding-3-small"]').setValue('text-embedding-3-small')

    // Index 2: VCS Save (0), Check for updates now (1), Embedding Save (2).
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[2].trigger('click')
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
    await flushPromises()

    await wrapper.get('select[aria-label="Embedding provider"]').setValue('Ollama')
    // Index 2: VCS Save (0), Check for updates now (1), Embedding Save (2).
    const saveButtons = wrapper.findAll('.save-row button')
    await saveButtons[2].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('the embedding probe failed: connection refused')
  })

  it('loads and pretty-prints the raw config on first expand', async () => {
    mockedGetRaw.mockResolvedValue('{"Vcs":{"WorkspaceRoot":"/tmp"}}')

    const wrapper = mount(SettingsPanel)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    expect(mockedGetRaw).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.raw-json').element.value).toContain('"WorkspaceRoot": "/tmp"')
  })

  it('rejects invalid JSON in the raw editor without calling the API', async () => {
    const wrapper = mount(SettingsPanel)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('{ not valid json')
    await wrapper.get('.section:last-of-type .save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateRaw).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Invalid JSON')
  })

  it('saves valid raw JSON and refreshes the structured sections', async () => {
    mockedUpdateRaw.mockResolvedValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    mockedGetVcs.mockResolvedValueOnce(vcs()).mockResolvedValueOnce(vcs({ workspaceRoot: '/tmp/new' }))

    const wrapper = mount(SettingsPanel)
    await flushPromises()
    await wrapper.get('.subsection-toggle').trigger('click')

    await wrapper.get('.raw-json').setValue('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    await wrapper.get('.section:last-of-type .save-row button').trigger('click')
    await flushPromises()

    expect(mockedUpdateRaw).toHaveBeenCalledWith('{"Vcs":{"WorkspaceRoot":"/tmp/new"}}')
    expect(wrapper.text()).toContain('Saved')
    expect(wrapper.get('input[placeholder*="workspaces"]').element.value).toBe('/tmp/new')
  })

  it('renders the Ollama model picker with models fetched from the backend', async () => {
    mockedGetEmbedding.mockResolvedValue(embedding({ provider: 'Ollama' }))
    mockedGetOllamaModels.mockResolvedValue(['nomic-embed-text:latest', 'llama3:8b'])

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    expect(mockedGetOllamaModels).toHaveBeenCalled()
    const select = wrapper.get('select[aria-label="Ollama model"]')
    const options = select.findAll('option').map((o) => o.text())
    expect(options).toContain('nomic-embed-text:latest')
    expect(options).toContain('llama3:8b')
  })

  it('fetches the model list when switching the provider to Ollama', async () => {
    mockedGetOllamaModels.mockResolvedValue(['nomic-embed-text'])

    const wrapper = mount(SettingsPanel)
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

    const wrapper = mount(SettingsPanel)
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

    const wrapper = mount(SettingsPanel)
    await flushPromises()

    await wrapper.get('input[aria-label="Model to pull"]').setValue('another')
    await wrapper.get('.pull-button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('A model pull is already running.')
  })
})
