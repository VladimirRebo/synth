import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import App from './App.vue'

describe('App', () => {
  it('renders the Synth heading', () => {
    const wrapper = mount(App)
    expect(wrapper.get('h1').text()).toBe('Synth')
  })

  it('renders the index and search panels', () => {
    const wrapper = mount(App)
    expect(wrapper.findAll('h2').map((h2) => h2.text())).toEqual(['Index a directory', 'Search'])
  })
})
