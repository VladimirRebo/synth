import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import LogsPanel from './LogsPanel.vue'
import * as api from '../api'

vi.mock('../api')

const mockedGetLogs = vi.mocked(api.getLogs)

function entry(overrides: Partial<api.LogEntry> = {}): api.LogEntry {
  return {
    timestamp: '2026-07-09T12:00:00Z',
    level: 'Information',
    message: 'hello world',
    exception: null,
    ...overrides,
  }
}

beforeEach(() => {
  vi.useFakeTimers()
  mockedGetLogs.mockReset()
  mockedGetLogs.mockResolvedValue([entry()])
})

afterEach(() => {
  vi.useRealTimers()
})

describe('LogsPanel', () => {
  it('loads and renders logs on mount', async () => {
    const wrapper = mount(LogsPanel)
    await flushPromises()

    expect(mockedGetLogs).toHaveBeenCalledWith({ level: undefined, search: undefined }, expect.any(AbortSignal))
    expect(wrapper.text()).toContain('hello world')
    expect(wrapper.text()).toContain('Information')
  })

  it('polls again after the interval elapses while auto-refresh is on', async () => {
    mount(LogsPanel)
    await flushPromises()
    expect(mockedGetLogs).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(3000)
    expect(mockedGetLogs).toHaveBeenCalledTimes(2)
  })

  it('stops polling when auto-refresh is unchecked', async () => {
    const wrapper = mount(LogsPanel)
    await flushPromises()

    await wrapper.get('.auto-refresh input').setValue(false)
    mockedGetLogs.mockClear()

    await vi.advanceTimersByTimeAsync(6000)
    expect(mockedGetLogs).not.toHaveBeenCalled()
  })

  it('refetches with the level filter applied', async () => {
    const wrapper = mount(LogsPanel)
    await flushPromises()
    mockedGetLogs.mockClear()

    await wrapper.get('select[aria-label="Minimum log level"]').setValue('Warning')
    await flushPromises()

    expect(mockedGetLogs).toHaveBeenCalledWith({ level: 'Warning', search: undefined }, expect.any(AbortSignal))
  })

  it('shows an empty state when there are no entries', async () => {
    mockedGetLogs.mockResolvedValue([])
    const wrapper = mount(LogsPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('No log entries yet.')
  })

  it('renders an exception block when present', async () => {
    mockedGetLogs.mockResolvedValue([entry({ level: 'Error', exception: 'System.Exception: boom' })])
    const wrapper = mount(LogsPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('System.Exception: boom')
  })

  it('does not let a slow, superseded request overwrite a faster newer one', async () => {
    // Regression test: fetchLogs used to have no request-sequencing guard, so an older in-flight
    // request resolving after a newer one would clobber the display with stale-filter results.
    let resolveFirst!: (entries: api.LogEntry[]) => void
    const firstCall = new Promise<api.LogEntry[]>((resolve) => {
      resolveFirst = resolve
    })
    mockedGetLogs.mockReset()
    mockedGetLogs
      .mockReturnValueOnce(firstCall) // mount's own fetch (level: '') — resolves last
      .mockResolvedValueOnce([entry({ message: 'warning entry' })]) // filter-change fetch — resolves first

    const wrapper = mount(LogsPanel)
    await wrapper.get('select[aria-label="Minimum log level"]').setValue('Warning')
    await flushPromises()

    expect(wrapper.text()).toContain('warning entry')

    resolveFirst([entry({ message: 'stale unfiltered entry' })])
    await flushPromises()

    expect(wrapper.text()).toContain('warning entry')
    expect(wrapper.text()).not.toContain('stale unfiltered entry')
  })
})
