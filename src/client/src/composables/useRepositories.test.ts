import { describe, it, expect, vi, beforeEach } from 'vitest'
import { useRepositories } from './useRepositories'
import * as api from '../api'

vi.mock('../api')

const mockedList = vi.mocked(api.listRepositories)

function repo(collection: string): api.RepositoryEntry {
  return {
    collection,
    sourceType: 'local',
    source: `/tmp/${collection}`,
    branch: null,
    lastIndexedAt: '2026-01-01T00:00:00Z',
    chunkCount: 1,
  }
}

beforeEach(() => {
  mockedList.mockReset()
})

// useRepositories is a module-level singleton (shared ref across every call, by design — see the
// composable's own comment), so these tests only assert observable state transitions rather than
// a fresh "nothing loaded yet" starting point, which would be true only for the very first test.
describe('useRepositories', () => {
  it('populates repositories and sets loaded on a successful refresh', async () => {
    mockedList.mockResolvedValue([repo('a'), repo('b')])

    const { repositories, loaded, error, refresh } = useRepositories()
    await refresh()

    expect(loaded.value).toBe(true)
    expect(error.value).toBe('')
    expect(repositories.value.map((r) => r.collection)).toEqual(['a', 'b'])
  })

  it('surfaces the error and keeps the previous repositories on a failed refresh', async () => {
    mockedList.mockResolvedValueOnce([repo('kept')])
    const first = useRepositories()
    await first.refresh()

    mockedList.mockRejectedValueOnce(new Error('network down'))
    const { repositories, loaded, error, refresh } = useRepositories()
    await refresh()

    expect(loaded.value).toBe(true)
    expect(error.value).toBe('network down')
    expect(repositories.value.map((r) => r.collection)).toEqual(['kept']) // stale-but-known, not wiped
  })

  it('clears a previous error once a later refresh succeeds', async () => {
    mockedList.mockRejectedValueOnce(new Error('network down'))
    const { error, refresh } = useRepositories()
    await refresh()
    expect(error.value).toBe('network down')

    mockedList.mockResolvedValueOnce([repo('recovered')])
    await refresh()

    expect(error.value).toBe('')
  })

  it('treats a non-array response as empty rather than throwing', async () => {
    // @ts-expect-error deliberately malformed to exercise the Array.isArray guard
    mockedList.mockResolvedValueOnce(null)

    const { repositories, error, refresh } = useRepositories()
    await refresh()

    expect(repositories.value).toEqual([])
    expect(error.value).toBe('')
  })
})
