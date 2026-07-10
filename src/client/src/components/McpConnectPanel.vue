<script setup lang="ts">
import { ref } from 'vue'
import Icon from './Icon.vue'

// Ready-to-copy MCP setup snippets, adapted from Sonar's McpSetup.vue pattern but much
// smaller — three tools, two transports. Descriptions mirror each tool's own [Description]
// attribute verbatim: Synth.Api/Mcp/CodeSearchTool.cs (search_code) and
// Synth.Api/Graph/CallGraphTool.cs (find_callers/find_callees, added by issue #33 — this list
// went stale once before when that tool shipped without a client-side update, keep it in sync).
const HTTP_SNIPPET = 'claude mcp add --transport http synth http://localhost:5042/mcp'
const STDIO_SNIPPET = 'claude mcp add synth -- dotnet run --project src/Synth.Mcp.Stdio'

interface ToolDoc {
  name: string
  description: string
}

const TOOLS: ToolDoc[] = [
  {
    name: 'search_code',
    description:
      "Search the indexed codebase for the code most relevant to a natural-language or keyword " +
      "query. Returns each hit's file path, class/method name and a source snippet.",
  },
  {
    name: 'find_callers',
    description:
      'Find the call sites that call a given symbol (its callers) using Synth’s structural ' +
      'call graph — exact, unlike vector search. Returns one edge per call site (caller, callee, ' +
      'source file and line).',
  },
  {
    name: 'find_callees',
    description:
      'Find the symbols a given symbol calls (its callees) using Synth’s structural call ' +
      'graph — exact, unlike vector search. Returns one edge per call site (caller, callee, ' +
      'source file and line).',
  },
]

const transport = ref<'http' | 'stdio'>('http')
const copied = ref(false)
const copyFailed = ref(false)

async function copy(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = true
    setTimeout(() => {
      copied.value = false
    }, 2000)
  } catch {
    // Clipboard write can be denied (permissions policy, non-secure context, automated
    // browser contexts) — fail visibly instead of an unhandled rejection.
    copyFailed.value = true
    setTimeout(() => {
      copyFailed.value = false
    }, 2000)
  }
}
</script>

<template>
  <section class="panel">
    <h2><Icon name="plug" :size="18" class="plug-icon" /> Connect via MCP</h2>
    <p class="intro">
      Synth exposes {{ TOOLS.length }} tools so an AI agent can search and navigate this codebase
      directly — semantic search plus an exact structural call graph. Point Claude Code (or any
      MCP client) at it:
    </p>

    <div class="transport-toggle" role="tablist">
      <button
        type="button"
        role="tab"
        :aria-selected="transport === 'http'"
        :class="{ active: transport === 'http' }"
        @click="transport = 'http'"
      >
        HTTP
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="transport === 'stdio'"
        :class="{ active: transport === 'stdio' }"
        @click="transport = 'stdio'"
      >
        stdio
      </button>
    </div>

    <div v-if="transport === 'http'" class="snippet-row">
      <pre class="snippet"><code>{{ HTTP_SNIPPET }}</code></pre>
      <button type="button" class="copy-button" @click="copy(HTTP_SNIPPET)">
        <Icon name="copy" :size="14" />
        {{ copied ? 'Copied!' : copyFailed ? 'Copy failed' : 'Copy' }}
      </button>
    </div>
    <div v-else class="snippet-row">
      <pre class="snippet"><code>{{ STDIO_SNIPPET }}</code></pre>
      <button type="button" class="copy-button" @click="copy(STDIO_SNIPPET)">
        <Icon name="copy" :size="14" />
        {{ copied ? 'Copied!' : copyFailed ? 'Copy failed' : 'Copy' }}
      </button>
    </div>
    <p class="hint">
      {{
        transport === 'http'
          ? 'Works while the API is running (e.g. via `make aspire`).'
          : 'Run from the repo root; no running API/Aspire needed — the stdio host is self-contained.'
      }}
    </p>

    <dl class="tools">
      <template v-for="tool in TOOLS" :key="tool.name">
        <dt><code>{{ tool.name }}</code></dt>
        <dd>{{ tool.description }}</dd>
      </template>
    </dl>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
}

h2 {
  display: flex;
  align-items: center;
  gap: 8px;
}

.plug-icon {
  color: var(--accent);
}

.intro {
  color: var(--text);
  font-size: 14px;
  margin-bottom: 12px;
}

.transport-toggle {
  display: flex;
  gap: 4px;
  margin-bottom: 8px;
}

.transport-toggle button {
  font: inherit;
  font-size: 13px;
  padding: 4px 12px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text);
  cursor: pointer;
}

.transport-toggle button.active {
  border-color: var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
  font-weight: 600;
}

.snippet-row {
  display: flex;
  align-items: stretch;
  gap: 8px;
}

.snippet {
  flex: 1;
  margin: 0;
  padding: 12px;
  overflow-x: auto;
  background: var(--code-bg);
  border-radius: 6px;
}

.snippet code {
  padding: 0;
  background: none;
  font-size: 13px;
  white-space: pre;
}

.copy-button {
  display: flex;
  align-items: center;
  gap: 6px;
  font: inherit;
  font-size: 13px;
  padding: 0 12px;
  border-radius: 6px;
  border: 1px solid var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
  cursor: pointer;
  white-space: nowrap;
}

.hint {
  font-size: 12px;
  color: var(--text);
  margin: 8px 0 0;
}

.tools {
  margin: 16px 0 0;
}

.tools dt {
  margin-bottom: 4px;
}

.tools dd {
  margin: 0;
  font-size: 13px;
  color: var(--text);
}
</style>
