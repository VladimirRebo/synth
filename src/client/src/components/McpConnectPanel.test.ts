import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import McpConnectPanel from './McpConnectPanel.vue'

beforeEach(() => {
  Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } })
})

describe('McpConnectPanel', () => {
  it('shows the HTTP snippet by default and the search_code tool description', () => {
    const wrapper = mount(McpConnectPanel)

    expect(wrapper.text()).toContain('claude mcp add --transport http synth')
    expect(wrapper.text()).toContain('search_code')
  })

  it('switches to the stdio snippet', async () => {
    const wrapper = mount(McpConnectPanel)

    await wrapper.get('button[role="tab"]:last-of-type').trigger('click')

    expect(wrapper.text()).toContain('dotnet run --project src/Synth.Mcp.Stdio')
    expect(wrapper.text()).not.toContain('claude mcp add --transport http synth')
  })

  it('copies the visible snippet to the clipboard and shows feedback', async () => {
    const wrapper = mount(McpConnectPanel)

    await wrapper.get('.copy-button').trigger('click')
    await flushPromises()

    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
      'claude mcp add --transport http synth http://localhost:5042/mcp',
    )
    expect(wrapper.get('.copy-button').text()).toContain('Copied!')
  })

  it('shows a failure state instead of an unhandled rejection when the clipboard write is denied', async () => {
    Object.assign(navigator, {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    })
    const wrapper = mount(McpConnectPanel)

    await wrapper.get('.copy-button').trigger('click')
    await flushPromises()

    expect(wrapper.get('.copy-button').text()).toContain('Copy failed')
  })
})
