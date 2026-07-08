import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import App from './App.vue'

describe('App', () => {
  it('renders the Synth heading', () => {
    const wrapper = mount(App)
    expect(wrapper.get('h1').text()).toBe('Synth')
  })

  it('renders the index, MCP connect and search panels', () => {
    const wrapper = mount(App)
    expect(wrapper.findAll('h2').map((h2) => h2.text())).toEqual([
      'Index a directory',
      'Connect via MCP',
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
