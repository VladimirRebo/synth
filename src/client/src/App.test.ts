import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import App from './App.vue'
import { routes } from './router'
import * as api from './api'

vi.mock('./api')

beforeEach(() => {
  vi.mocked(api.listRepositories).mockResolvedValue([])
  vi.mocked(api.getVcsSettings).mockResolvedValue({
    workspaceRoot: null,
    github: { tokenSet: false },
    gitlab: { tokenSet: false },
  })
  vi.mocked(api.getEmbeddingSettings).mockResolvedValue({
    provider: null,
    ollama: { endpoint: null, model: null },
    openai: { apiKeySet: false, model: null },
  })
  vi.mocked(api.getRawSettings).mockResolvedValue('{}')
  vi.mocked(api.getLogs).mockResolvedValue([])
})

// App.vue relies on vue-router (Sidebar's router-link/router-view, Cmd+K navigation), so
// every mount needs an isolated router in context — a fresh memory-history instance per test
// avoids routes/focus state leaking between cases.
async function mountApp(initialPath = '/search') {
  const router = createRouter({ history: createMemoryHistory(), routes })
  router.push(initialPath)
  await router.isReady()
  const wrapper = mount(App, { global: { plugins: [router] }, attachTo: document.body })
  await flushPromises()
  return { wrapper, router }
}

describe('App', () => {
  it('renders the Synth brand and the nav links for every page', async () => {
    const { wrapper } = await mountApp()

    expect(wrapper.get('.brand').text()).toBe('Synth')
    expect(wrapper.findAll('.nav-link').map((l) => l.text())).toEqual([
      'Search',
      'Index',
      'MCP',
      'Settings',
      'Logs',
    ])
    wrapper.unmount()
  })

  it('redirects / to the Search page by default', async () => {
    const { wrapper, router } = await mountApp('/')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('search')
    expect(wrapper.find('input[aria-label="Search query"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('navigates to another page when its nav link is clicked', async () => {
    const { wrapper } = await mountApp()

    const settingsLink = wrapper.findAll('.nav-link').find((l) => l.text() === 'Settings')!
    await settingsLink.trigger('click')
    await flushPromises()

    expect(wrapper.get('h2').text()).toContain('Settings')
    wrapper.unmount()
  })

  it('toggles the theme and updates the document attribute', async () => {
    const { wrapper } = await mountApp()
    const initial = document.documentElement.getAttribute('data-theme')

    await wrapper.get('.theme-toggle').trigger('click')

    expect(document.documentElement.getAttribute('data-theme')).not.toBe(initial)
    wrapper.unmount()
  })

  it('Cmd+K jumps to Search and focuses the query input from another page', async () => {
    const { wrapper, router } = await mountApp('/settings')
    expect(router.currentRoute.value.name).toBe('settings')

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true }))
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('search')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Search query')
    wrapper.unmount()
  })
})
