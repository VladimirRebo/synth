import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import SearchPanel from './components/SearchPanel.vue'
import BrowsePanel from './components/BrowsePanel.vue'
import IndexPanel from './components/IndexPanel.vue'
import McpConnectPanel from './components/McpConnectPanel.vue'
import SettingsPanel from './components/SettingsPanel.vue'
import LogsPanel from './components/LogsPanel.vue'

// Exported separately (not just inlined into createRouter below) so tests can build an
// isolated memory-history router from the exact same route table instead of duplicating it.
export const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/search' },
  { path: '/search', name: 'search', component: SearchPanel },
  { path: '/browse', name: 'browse', component: BrowsePanel },
  { path: '/index', name: 'index', component: IndexPanel },
  { path: '/mcp', name: 'mcp', component: McpConnectPanel },
  { path: '/settings', name: 'settings', component: SettingsPanel },
  { path: '/logs', name: 'logs', component: LogsPanel },
]

// Hash-based history (not createWebHistory): vite.config.ts deliberately uses a relative
// build base (`base: './'`, see SYNTH-15) so one build is deployable under any sub-path —
// the History API needs an absolute base to resolve routes against, which a relative build
// base can't reliably supply. A hash (`#/search`) sidesteps that entirely: it never touches
// the server-resolved path, so it works identically regardless of where dist/ is served from.
export const router = createRouter({
  history: createWebHashHistory(),
  routes,
})
