import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import BrowsePanel from './BrowsePanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetFileChunks = vi.mocked(api.getFileChunks)
const mockedListRepositories = vi.mocked(api.listRepositories)

function chunk(overrides: Partial<api.FileChunk> = {}): api.FileChunk {
  return {
    chunkType: 'Method',
    className: 'Calculator',
    methodName: 'Add',
    qualifiedName: 'Sample.Calculator.Add',
    startLine: 5,
    endLine: 5,
    content: 'public int Add(int a, int b) => a + b;',
    summary: null,
    embeddingText: '[code]\nSample.Calculator.Add\npublic int Add(int a, int b) => a + b;',
    ...overrides,
  }
}

function repo(overrides: Partial<api.RepositoryEntry> = {}): api.RepositoryEntry {
  return {
    collection: 'local-my-project',
    sourceType: 'local',
    source: '/local/my-project',
    branch: null,
    lastIndexedAt: '2026-07-10T00:00:00Z',
    chunkCount: 1,
    ...overrides,
  }
}

beforeEach(() => {
  mockedGetFileChunks.mockReset()
  mockedListRepositories.mockReset()
  // A single indexed collection, so the panel auto-selects it when the picker is left blank —
  // mirrors GET /search's own CollectionNameResolver sole-collection fallback.
  mockedListRepositories.mockResolvedValue([repo()])
})

async function browse(wrapper: ReturnType<typeof mount>, path = 'src/Calculator.cs') {
  await wrapper.get('input[aria-label="Relative file path"]').setValue(path)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('BrowsePanel', () => {
  it('fetches and renders a file\'s chunks, including the embedding text', async () => {
    mockedGetFileChunks.mockResolvedValue([
      chunk(),
      chunk({
        methodName: 'Subtract',
        qualifiedName: 'Sample.Calculator.Subtract',
        startLine: 7,
        endLine: 7,
        content: 'public int Subtract(int a, int b) => a - b;',
        embeddingText: '[code]\nSample.Calculator.Subtract\npublic int Subtract(int a, int b) => a - b;',
      }),
    ])

    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await browse(wrapper)

    // Empty picker selection auto-resolves to the sole indexed collection.
    expect(mockedGetFileChunks).toHaveBeenCalledWith('local-my-project', 'src/Calculator.cs')
    expect(wrapper.text()).toContain('2 chunks')
    expect(wrapper.text()).toContain('Sample.Calculator.Add')
    expect(wrapper.text()).toContain('Sample.Calculator.Subtract')
    // The embedding text (the point of the panel) is shown, prefix and all.
    expect(wrapper.text()).toContain('[code]')
    expect(wrapper.text()).toContain('L5–5')
  })

  it('passes the selected collection through to the API', async () => {
    mockedListRepositories.mockResolvedValue([
      {
        collection: 'github-com-owner-repo',
        sourceType: 'github',
        source: 'https://github.com/owner/repo',
        branch: 'main',
        lastIndexedAt: '2026-07-10T00:00:00Z',
        chunkCount: 3,
      },
    ])
    mockedGetFileChunks.mockResolvedValue([chunk()])

    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await wrapper.get('select[aria-label="Collection to browse"]').setValue('github-com-owner-repo')
    await browse(wrapper, 'Program.cs')

    expect(mockedGetFileChunks).toHaveBeenCalledWith('github-com-owner-repo', 'Program.cs')
  })

  it('shows an empty state when the file has no chunks', async () => {
    mockedGetFileChunks.mockResolvedValue([])

    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await browse(wrapper, 'Missing.cs')

    expect(wrapper.text()).toContain('No chunks for')
    expect(wrapper.text()).toContain('Missing.cs')
  })

  it('does not submit for a blank path', async () => {
    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedGetFileChunks).not.toHaveBeenCalled()
  })

  it('requires an explicit collection choice when more than one is indexed', async () => {
    mockedListRepositories.mockResolvedValue([
      repo({ collection: 'local-project-a' }),
      repo({ collection: 'local-project-b', source: '/local/project-b' }),
    ])

    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await browse(wrapper)

    expect(mockedGetFileChunks).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('Pick a collection')
  })

  it('shows an error message when the fetch fails', async () => {
    mockedGetFileChunks.mockRejectedValue(new Error('Loading file chunks failed (500)'))

    const wrapper = mount(BrowsePanel)
    await flushPromises()
    await browse(wrapper)

    expect(wrapper.get('[role="alert"]').text()).toBe('Loading file chunks failed (500)')
  })
})
