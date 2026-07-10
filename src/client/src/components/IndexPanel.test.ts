import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import IndexPanel from './IndexPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedIndexSource = vi.mocked(api.indexSource)
const mockedGetIndexStatus = vi.mocked(api.getIndexStatus)
const mockedListRepositories = vi.mocked(api.listRepositories)

function job(overrides: Partial<api.IndexJobStatus> = {}): api.IndexJobStatus {
  return {
    state: 'Idle',
    collection: '',
    source: '',
    filesIndexed: 0,
    filesSkipped: 0,
    totalFiles: null,
    chunksIndexed: 0,
    startedAt: null,
    finishedAt: null,
    error: null,
    ...overrides,
  }
}

beforeEach(() => {
  mockedIndexSource.mockReset()
  mockedGetIndexStatus.mockReset()
  mockedListRepositories.mockReset()
  mockedListRepositories.mockResolvedValue([])
  mockedGetIndexStatus.mockResolvedValue(job()) // idle by default (mount's own poll)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('IndexPanel', () => {
  it('starts indexing and shows the summary once the job reports Done', async () => {
    mockedIndexSource.mockResolvedValue({ collection: 'default', status: 'started' })
    mockedGetIndexStatus
      .mockResolvedValueOnce(job()) // mount poll: idle
      .mockResolvedValueOnce(job({ state: 'Done', filesIndexed: 3, filesSkipped: 1, chunksIndexed: 12 }))

    const wrapper = mount(IndexPanel)
    await flushPromises()
    await wrapper.get('input[aria-label="Directory path"]').setValue('/repo/src')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).toHaveBeenCalledWith({ path: '/repo/src' })
    expect(wrapper.text()).toContain('Indexed 3 files')
    expect(wrapper.text()).toContain('12 chunks')
    expect(wrapper.text()).toContain('1 skipped')
    expect(wrapper.get('.status').text()).toBe('Done')
  })

  it('shows an error message when starting the job fails (e.g. 400/409)', async () => {
    mockedIndexSource.mockRejectedValue(new Error('Directory not found: /nope'))

    const wrapper = mount(IndexPanel)
    await flushPromises()
    await wrapper.get('input[aria-label="Directory path"]').setValue('/nope')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Directory not found: /nope')
    expect(wrapper.get('.status').text()).toBe('Error')
  })

  it('does not submit for a blank path', async () => {
    const wrapper = mount(IndexPanel)
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).not.toHaveBeenCalled()
  })

  it('switches to repository-URL mode and calls indexSource with repoUrl + branch', async () => {
    mockedIndexSource.mockResolvedValue({ collection: 'github-com-owner-repo', status: 'started' })
    mockedGetIndexStatus
      .mockResolvedValueOnce(job())
      .mockResolvedValueOnce(job({ state: 'Done', filesIndexed: 5, chunksIndexed: 20 }))

    const wrapper = mount(IndexPanel)
    await flushPromises()
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
    await flushPromises()
    await wrapper.get('[role="tab"]:nth-of-type(2)').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mockedIndexSource).not.toHaveBeenCalled()
  })

  it('shows an empty state when nothing has been indexed', async () => {
    const wrapper = mount(IndexPanel)
    await flushPromises()

    expect(mockedListRepositories).toHaveBeenCalled()
    expect(wrapper.text()).toContain('Nothing indexed yet.')
  })

  it('lists indexed repositories with their collection and source', async () => {
    mockedListRepositories.mockResolvedValue([
      {
        collection: 'default',
        sourceType: 'local',
        source: '/Users/vladimir/RiderProjects/synth',
        branch: null,
        lastIndexedAt: '2026-07-10T00:00:00Z',
        chunkCount: 561,
      },
      {
        collection: 'github-com-owner-repo',
        sourceType: 'github',
        source: 'https://github.com/owner/repo',
        branch: 'main',
        lastIndexedAt: '2026-07-10T01:00:00Z',
        chunkCount: 42,
      },
    ])

    const wrapper = mount(IndexPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('default')
    expect(wrapper.text()).toContain('/Users/vladimir/RiderProjects/synth')
    expect(wrapper.text()).toContain('561 chunks')
    expect(wrapper.text()).toContain('github-com-owner-repo')
    expect(wrapper.text()).toContain('main')
    expect(wrapper.text()).toContain('42 chunks')
  })

  it('resumes an in-flight job on mount, even without submitting — this is what survives a reload', async () => {
    mockedGetIndexStatus.mockResolvedValue(
      job({ state: 'Running', filesIndexed: 4, totalFiles: 10, collection: 'default' }),
    )

    const wrapper = mount(IndexPanel)
    await flushPromises()

    expect(mockedIndexSource).not.toHaveBeenCalled()
    expect(wrapper.get('.status').text()).toBe('Indexing… 4/10 files')
  })

  it('polls again after the interval while a job is running, then stops once Done', async () => {
    vi.useFakeTimers()
    mockedIndexSource.mockResolvedValue({ collection: 'default', status: 'started' })
    mockedGetIndexStatus
      .mockResolvedValueOnce(job()) // mount poll: idle
      .mockResolvedValueOnce(job({ state: 'Running', filesIndexed: 1, totalFiles: 3 })) // right after submit
      .mockResolvedValueOnce(job({ state: 'Running', filesIndexed: 2, totalFiles: 3 })) // one interval later
      .mockResolvedValueOnce(job({ state: 'Done', filesIndexed: 3, totalFiles: 3, chunksIndexed: 9 }))

    const wrapper = mount(IndexPanel)
    await flushPromises()
    await wrapper.get('input[aria-label="Directory path"]').setValue('/repo/src')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('.status').text()).toBe('Indexing… 1/3 files')

    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.get('.status').text()).toBe('Indexing… 2/3 files')

    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.get('.status').text()).toBe('Done')

    mockedGetIndexStatus.mockClear()
    await vi.advanceTimersByTimeAsync(3000)
    expect(mockedGetIndexStatus).not.toHaveBeenCalled()
  })

  it('disables the submit button while a job is running', async () => {
    mockedGetIndexStatus.mockResolvedValue(job({ state: 'Running' }))

    const wrapper = mount(IndexPanel)
    await flushPromises()
    await wrapper.get('input[aria-label="Directory path"]').setValue('/repo/src')

    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
  })
})
