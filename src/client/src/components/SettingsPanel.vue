<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  getVcsSettings,
  updateVcsSettings,
  getEmbeddingSettings,
  updateEmbeddingSettings,
  type VcsSettings,
  type EmbeddingSettings,
  type VcsSettingsPatch,
  type EmbeddingSettingsPatch,
} from '../api'
import Icon from './Icon.vue'

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error'

const expanded = ref(false)
const loaded = ref(false)
const loadError = ref('')

const vcs = ref<VcsSettings | null>(null)
const workspaceRoot = ref('')
const githubToken = ref('')
const githubClear = ref(false)
const gitlabToken = ref('')
const gitlabClear = ref(false)
const vcsStatus = ref<SaveStatus>('idle')
const vcsError = ref('')

const embedding = ref<EmbeddingSettings | null>(null)
const provider = ref<'' | 'Ollama' | 'OpenAI'>('')
const ollamaEndpoint = ref('')
const ollamaModel = ref('')
const openaiKey = ref('')
const openaiKeyClear = ref(false)
const openaiModel = ref('')
const embeddingStatus = ref<SaveStatus>('idle')
const embeddingError = ref('')

async function loadAll() {
  loadError.value = ''
  try {
    const [vcsSettings, embeddingSettings] = await Promise.all([getVcsSettings(), getEmbeddingSettings()])
    applyVcs(vcsSettings)
    applyEmbedding(embeddingSettings)
    loaded.value = true
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  }
}

function applyVcs(settings: VcsSettings) {
  vcs.value = settings
  workspaceRoot.value = settings.workspaceRoot ?? ''
  githubToken.value = ''
  githubClear.value = false
  gitlabToken.value = ''
  gitlabClear.value = false
}

function applyEmbedding(settings: EmbeddingSettings) {
  embedding.value = settings
  provider.value = settings.provider ?? ''
  ollamaEndpoint.value = settings.ollama.endpoint ?? ''
  ollamaModel.value = settings.ollama.model ?? ''
  openaiKey.value = ''
  openaiKeyClear.value = false
  openaiModel.value = settings.openai.model ?? ''
}

async function toggle() {
  expanded.value = !expanded.value
  if (expanded.value && !loaded.value) await loadAll()
}

async function saveVcs() {
  if (!vcs.value) return

  const patch: VcsSettingsPatch = {}
  if (workspaceRoot.value.trim() !== (vcs.value.workspaceRoot ?? '')) {
    patch.workspaceRoot = workspaceRoot.value.trim() || null
  }
  if (githubClear.value) patch.github = { token: '' }
  else if (githubToken.value) patch.github = { token: githubToken.value }
  if (gitlabClear.value) patch.gitlab = { token: '' }
  else if (gitlabToken.value) patch.gitlab = { token: gitlabToken.value }

  vcsStatus.value = 'saving'
  vcsError.value = ''
  try {
    const updated = await updateVcsSettings(patch)
    applyVcs(updated)
    vcsStatus.value = 'saved'
  } catch (err) {
    vcsError.value = err instanceof Error ? err.message : String(err)
    vcsStatus.value = 'error'
  }
}

async function saveEmbedding() {
  if (!embedding.value) return

  const patch: EmbeddingSettingsPatch = {}
  if (provider.value !== (embedding.value.provider ?? '')) patch.provider = provider.value || null

  if (provider.value === 'Ollama') {
    patch.ollama = {
      endpoint: ollamaEndpoint.value.trim() || null,
      model: ollamaModel.value.trim() || null,
    }
  }

  if (provider.value === 'OpenAI') {
    patch.openai = { model: openaiModel.value.trim() || null }
    if (openaiKeyClear.value) patch.openai.apiKey = ''
    else if (openaiKey.value) patch.openai.apiKey = openaiKey.value
  }

  embeddingStatus.value = 'saving'
  embeddingError.value = ''
  try {
    const updated = await updateEmbeddingSettings(patch)
    applyEmbedding(updated)
    embeddingStatus.value = 'saved'
  } catch (err) {
    embeddingError.value = err instanceof Error ? err.message : String(err)
    embeddingStatus.value = 'error'
  }
}

const providerLabel = computed(() =>
  provider.value === 'OpenAI' ? 'OpenAI' : provider.value === 'Ollama' ? 'Ollama' : 'Default (Aspire Ollama)',
)
</script>

<template>
  <section class="panel">
    <button type="button" class="panel-toggle" :aria-expanded="expanded" @click="toggle">
      <Icon name="sliders" :size="16" />
      <h2>Settings</h2>
      <Icon name="chevron-down" :size="16" class="chevron" :class="{ open: expanded }" />
    </button>

    <div v-if="expanded" class="body">
      <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

      <template v-else-if="loaded">
        <section class="section">
          <h3>VCS</h3>
          <div class="field">
            <label>Workspace root</label>
            <input v-model="workspaceRoot" type="text" placeholder="~/.synth/workspaces (default)" />
          </div>
          <div class="field">
            <label>GitHub token</label>
            <input
              v-model="githubToken"
              type="password"
              :disabled="githubClear"
              :placeholder="vcs?.github.tokenSet ? '•••• (set — leave blank to keep)' : 'not set'"
            />
            <label class="clear-toggle">
              <input v-model="githubClear" type="checkbox" :disabled="!vcs?.github.tokenSet" />
              Clear
            </label>
          </div>
          <div class="field">
            <label>GitLab token</label>
            <input
              v-model="gitlabToken"
              type="password"
              :disabled="gitlabClear"
              :placeholder="vcs?.gitlab.tokenSet ? '•••• (set — leave blank to keep)' : 'not set'"
            />
            <label class="clear-toggle">
              <input v-model="gitlabClear" type="checkbox" :disabled="!vcs?.gitlab.tokenSet" />
              Clear
            </label>
          </div>
          <div class="save-row">
            <button type="button" :disabled="vcsStatus === 'saving'" @click="saveVcs">
              {{ vcsStatus === 'saving' ? 'Saving…' : 'Save' }}
            </button>
            <span class="status" :class="vcsStatus">
              <template v-if="vcsStatus === 'saved'">Saved</template>
              <template v-else-if="vcsStatus === 'error'">{{ vcsError }}</template>
            </span>
          </div>
        </section>

        <section class="section">
          <h3>Embedding</h3>
          <div class="field">
            <label>Provider</label>
            <select v-model="provider" aria-label="Embedding provider">
              <option value="">Default (Aspire Ollama)</option>
              <option value="Ollama">Ollama</option>
              <option value="OpenAI">OpenAI</option>
            </select>
          </div>

          <template v-if="provider === 'Ollama'">
            <div class="field">
              <label>Endpoint</label>
              <input v-model="ollamaEndpoint" type="text" placeholder="uses the Aspire connection if blank" />
            </div>
            <div class="field">
              <label>Model</label>
              <input v-model="ollamaModel" type="text" placeholder="uses the Aspire connection if blank" />
            </div>
          </template>

          <template v-else-if="provider === 'OpenAI'">
            <div class="field">
              <label>API key</label>
              <input
                v-model="openaiKey"
                type="password"
                :disabled="openaiKeyClear"
                :placeholder="embedding?.openai.apiKeySet ? '•••• (set — leave blank to keep)' : 'not set'"
              />
              <label class="clear-toggle">
                <input v-model="openaiKeyClear" type="checkbox" :disabled="!embedding?.openai.apiKeySet" />
                Clear
              </label>
            </div>
            <div class="field">
              <label>Model</label>
              <input v-model="openaiModel" type="text" placeholder="text-embedding-3-small" />
            </div>
          </template>
          <p v-else class="hint">Currently: {{ providerLabel }}.</p>

          <div class="save-row">
            <button type="button" :disabled="embeddingStatus === 'saving'" @click="saveEmbedding">
              {{ embeddingStatus === 'saving' ? 'Saving…' : 'Save' }}
            </button>
            <span class="status" :class="embeddingStatus">
              <template v-if="embeddingStatus === 'saved'">Saved</template>
              <template v-else-if="embeddingStatus === 'error'">{{ embeddingError }}</template>
            </span>
          </div>
        </section>
      </template>
    </div>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
  border-bottom: 1px solid var(--border);
}

.panel-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  border: none;
  background: none;
  padding: 0;
  cursor: pointer;
  color: var(--text-h);
}

.panel-toggle h2 {
  margin: 0;
}

.chevron {
  margin-left: auto;
  transition: transform 0.15s;
  color: var(--text);
}

.chevron.open {
  transform: rotate(180deg);
}

.body {
  margin-top: 16px;
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.section h3 {
  margin: 0 0 12px;
  font-size: 14px;
  color: var(--text);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.field {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}

.field label:first-child {
  width: 120px;
  flex-shrink: 0;
  color: var(--text);
  font-size: 13px;
}

.field input[type='text'],
.field input[type='password'],
.field select {
  flex: 1;
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text-h);
}

.clear-toggle {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  color: var(--text);
  white-space: nowrap;
}

.hint {
  color: var(--text);
  font-size: 13px;
  margin: 0 0 10px;
}

.save-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.save-row button {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  cursor: pointer;
  border: 1px solid var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
}

.save-row button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.status {
  font-size: 12px;
  font-family: var(--mono);
}

.status.saved {
  color: var(--status-green);
}

.status.error {
  color: var(--status-red);
}

.error {
  color: var(--status-red);
}
</style>
