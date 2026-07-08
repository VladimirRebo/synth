import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import App from './App.vue'
import * as api from './api'

vi.mock('./api')

beforeEach(() => {
  vi.mocked(api.listRepositories).mockResolvedValue([])
})

describe('App', () => {
  it('renders the Synth heading', () => {
    const wrapper = mount(App)
    expect(wrapper.get('h1').text()).toBe('Synth')
  })

  it('renders the index, MCP connect and search panels', () => {
    const wrapper = mount(App)
    expect(wrapper.findAll('h2').map((h2) => h2.text())).toEqual([
      'Index a repository',
      'Connect via MCP',
      'Settings',
      'Logs',
      'Search',
    ])
  })

  it('toggles the theme and updates the document attribute', async () => {
    const wrapper = mount(App, { attachTo: document.body })
    const initial = document.documentElement.getAttribute('data-theme')

    await wrapper.get('.theme-toggle').trigger('click')

    expect(document.documentElement.getAttribute('data-theme')).not.toBe(initial)
    wrapper.unmount()
  })
})
