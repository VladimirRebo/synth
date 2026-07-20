import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import McpConnectPanel from './McpConnectPanel.vue'

beforeEach(() => {
  Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } })
})

describe('McpConnectPanel', () => {
  it('shows the HTTP snippet by default and every registered tool', () => {
    // This list has gone stale twice before (a tool shipped server-side without a matching
    // update here) — assert every tool actually registered on the MCP server (confirmed live
    // via a real tools/list call), not just a couple, so a future omission fails a test instead
    // of silently shipping.
    const wrapper = mount(McpConnectPanel)

    expect(wrapper.text()).toContain('claude mcp add --transport http synth')
    for (const tool of [
      'search_code',
      'get_symbol',
      'get_file',
      'find_callers',
      'find_callees',
      'index_code',
      'list_collections',
      'delete_collection',
      'health_check',
    ]) {
      expect(wrapper.text()).toContain(tool)
    }
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

  it('clears the pending "Copied!" reset timer on unmount', async () => {
    // Regression test: the reset timer used to have no handle/cleanup, so an unmount within the
    // 2s window left a dangling setTimeout — the same leaked-timer pattern this codebase guards
    // against elsewhere (IndexPanel.vue, LogsPanel.vue both clear their interval timers).
    const clearTimeoutSpy = vi.spyOn(globalThis, 'clearTimeout')
    const wrapper = mount(McpConnectPanel)

    await wrapper.get('.copy-button').trigger('click')
    await flushPromises()
    expect(wrapper.get('.copy-button').text()).toContain('Copied!')

    wrapper.unmount()

    expect(clearTimeoutSpy).toHaveBeenCalled()
  })
})
