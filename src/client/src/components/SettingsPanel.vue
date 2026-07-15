<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  getVcsSettings,
  updateVcsSettings,
  getEmbeddingSettings,
  updateEmbeddingSettings,
  getOllamaModels,
  pullOllamaModel,
  getOllamaPullStatus,
  getRawSettings,
  updateRawSettings,
  type VcsSettings,
  type EmbeddingSettings,
  type VcsSettingsPatch,
  type EmbeddingSettingsPatch,
  type OllamaPullStatus,
} from '../api'
import Icon from './Icon.vue'

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error'

const loaded = ref(false)
const loadError = ref('')

const vcs = ref<VcsSettings | null>(null)
const workspaceRoot = ref('')
const githubToken = ref('')
const githubClear = ref(false)
const githubWebhookSecret = ref('')
const githubWebhookSecretClear = ref(false)
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

// Ollama model picker (SYNTH-50): the locally-available models fetched from the live instance, plus
// the fire-and-forget "pull a new model" flow polled from the backend (mirrors IndexPanel's polling).
const POLL_INTERVAL_MS = 1000
const ollamaModels = ref<string[]>([])
const ollamaModelsError = ref('')
const pullModel = ref('')
const pullStarting = ref(false)
const pullError = ref('') // from the POST itself (400/409) — distinct from a Failed pull's own error
const pull = ref<OllamaPullStatus | null>(null)
let pullPollTimer: ReturnType<typeof setInterval> | null = null
let pullPollSeq = 0 // guards against an overlapping older poll resolving after a newer one

const rawExpanded = ref(false)
const rawJson = ref('')
const rawStatus = ref<SaveStatus>('idle')
const rawError = ref('')

async function loadAll() {
  loadError.value = ''
  try {
    const [vcsSettings, embeddingSettings, raw] = await Promise.all([
      getVcsSettings(),
      getEmbeddingSettings(),
      getRawSettings(),
    ])
    applyVcs(vcsSettings)
    applyEmbedding(embeddingSettings)
    applyRaw(raw)
    loaded.value = true
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  }
}

function applyRaw(json: string) {
  try {
    rawJson.value = JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    rawJson.value = json
  }
}

function applyVcs(settings: VcsSettings) {
  vcs.value = settings
  workspaceRoot.value = settings.workspaceRoot ?? ''
  githubToken.value = ''
  githubClear.value = false
  githubWebhookSecret.value = ''
  githubWebhookSecretClear.value = false
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

onMounted(() => {
  loadAll() // applyEmbedding sets `provider`, which the watch below turns into a model fetch when Ollama
  pollPull() // resumes an in-flight or just-finished pull even after a reload
})

onUnmounted(stopPullPolling)

// Fetch the model list whenever the section becomes relevant — i.e. `provider` turns to Ollama, whether
// from the initial load (applyEmbedding) or the user switching the dropdown. The section only renders
// then, so there's no point fetching for OpenAI/default. The endpoint resolves server-side from the
// *saved* config, so a save is what changes it — hence saveEmbedding refetches too.
watch(provider, (value) => {
  if (value === 'Ollama' && ollamaModels.value.length === 0) fetchOllamaModels()
})

async function fetchOllamaModels() {
  ollamaModelsError.value = ''
  try {
    ollamaModels.value = await getOllamaModels()
  } catch (err) {
    ollamaModelsError.value = err instanceof Error ? err.message : String(err)
  }
}

// The <select> options: the fetched models plus the currently-selected one (so a model saved earlier
// but no longer reported — or not yet listed — stays selectable rather than silently vanishing).
const ollamaModelOptions = computed(() => {
  const models = new Set(ollamaModels.value)
  if (ollamaModel.value) models.add(ollamaModel.value)
  return [...models]
})

async function pollPull() {
  const seq = ++pullPollSeq
  let result: OllamaPullStatus
  try {
    result = await getOllamaPullStatus()
  } catch {
    return // transient poll failure — keep the last known state, try again next tick
  }
  if (seq !== pullPollSeq) return // a newer poll already landed while this one was in flight — discard

  pull.value = result
  if (result.state === 'Running') {
    if (pullPollTimer === null) startPullPolling()
  } else {
    stopPullPolling()
    // A finished pull may have added the model locally — refresh the picker so it shows up.
    if (result.state === 'Done') fetchOllamaModels()
  }
}

function startPullPolling() {
  stopPullPolling()
  pullPollTimer = setInterval(pollPull, POLL_INTERVAL_MS)
}

function stopPullPolling() {
  if (pullPollTimer !== null) {
    clearInterval(pullPollTimer)
    pullPollTimer = null
  }
}

const pullBusy = computed(() => pullStarting.value || pull.value?.state === 'Running')

async function startPull() {
  const model = pullModel.value.trim()
  if (!model || pullBusy.value) return

  pullError.value = ''
  pullStarting.value = true
  try {
    await pullOllamaModel(model)
    await pollPull()
    pullModel.value = ''
  } catch (err) {
    pullError.value = err instanceof Error ? err.message : String(err)
  } finally {
    pullStarting.value = false
  }
}

const pullStatusText = computed(() => {
  if (pullError.value) return pullError.value
  const state = pull.value?.state
  if (state === 'Running') {
    const detail = pull.value?.status
    return detail ? `Pulling ${pull.value?.model}… ${detail}` : `Pulling ${pull.value?.model}…`
  }
  if (state === 'Done') return `Pulled ${pull.value?.model}.`
  if (state === 'Failed') return `Pull failed: ${pull.value?.error ?? 'unknown error'}`
  return ''
})

const pullStatusClass = computed(() =>
  pullError.value || pull.value?.state === 'Failed' ? 'error' : '',
)

async function saveVcs() {
  if (!vcs.value) return

  const patch: VcsSettingsPatch = {}
  if (workspaceRoot.value.trim() !== (vcs.value.workspaceRoot ?? '')) {
    patch.workspaceRoot = workspaceRoot.value.trim() || null
  }
  const github: { token?: string; webhookSecret?: string } = {}
  if (githubClear.value) github.token = ''
  else if (githubToken.value) github.token = githubToken.value
  if (githubWebhookSecretClear.value) github.webhookSecret = ''
  else if (githubWebhookSecret.value) github.webhookSecret = githubWebhookSecret.value
  if (Object.keys(github).length > 0) patch.github = github

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
    // The endpoint may have changed — refresh the model list against the just-saved Ollama instance.
    if (provider.value === 'Ollama') fetchOllamaModels()
  } catch (err) {
    embeddingError.value = err instanceof Error ? err.message : String(err)
    embeddingStatus.value = 'error'
  }
}

function toggleRaw() {
  rawExpanded.value = !rawExpanded.value
}

async function saveRaw() {
  let parsed: unknown
  try {
    parsed = JSON.parse(rawJson.value)
  } catch (err) {
    rawStatus.value = 'error'
    rawError.value = `Invalid JSON: ${err instanceof Error ? err.message : String(err)}`
    return
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    rawStatus.value = 'error'
    rawError.value = 'The document must be a JSON object.'
    return
  }

  rawStatus.value = 'saving'
  rawError.value = ''
  try {
    const persisted = await updateRawSettings(rawJson.value)
    applyRaw(persisted)
    // The raw document may have changed the Vcs/Embedding sections too — refresh their
    // structured views so they don't go stale relative to what was just saved.
    const [vcsSettings, embeddingSettings] = await Promise.all([getVcsSettings(), getEmbeddingSettings()])
    applyVcs(vcsSettings)
    applyEmbedding(embeddingSettings)
    rawStatus.value = 'saved'
  } catch (err) {
    rawError.value = err instanceof Error ? err.message : String(err)
    rawStatus.value = 'error'
  }
}

const providerLabel = computed(() =>
  provider.value === 'OpenAI' ? 'OpenAI' : provider.value === 'Ollama' ? 'Ollama' : 'Default (Aspire Ollama)',
)
</script>

<template>
  <section class="panel">
    <h2 class="panel-heading"><Icon name="sliders" :size="18" /> Settings</h2>

    <div class="body">
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
            <label>GitHub webhook secret</label>
            <input
              v-model="githubWebhookSecret"
              type="password"
              :disabled="githubWebhookSecretClear"
              :placeholder="vcs?.github.webhookSecretSet ? '•••• (set — leave blank to keep)' : 'not set'"
            />
            <label class="clear-toggle">
              <input
                v-model="githubWebhookSecretClear"
                type="checkbox"
                :disabled="!vcs?.github.webhookSecretSet"
              />
              Clear
            </label>
            <p class="hint">
              Paste the same secret into the repo's GitHub Settings → Webhooks → Add webhook (Payload
              URL: this server's <code>/webhooks/github</code>, content type
              <code>application/json</code>, event: <code>push</code>).
            </p>
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
              <select v-model="ollamaModel" aria-label="Ollama model">
                <option value="">uses the Aspire connection if blank</option>
                <option v-for="model in ollamaModelOptions" :key="model" :value="model">{{ model }}</option>
              </select>
            </div>
            <p v-if="ollamaModelsError" class="hint error">{{ ollamaModelsError }}</p>
            <div class="field">
              <label>Pull a model</label>
              <input
                v-model="pullModel"
                type="text"
                placeholder="e.g. nomic-embed-text"
                aria-label="Model to pull"
                @keyup.enter="startPull"
              />
              <button
                type="button"
                class="pull-button"
                :disabled="pullBusy || !pullModel.trim()"
                @click="startPull"
              >
                {{ pull?.state === 'Running' ? 'Pulling…' : 'Pull' }}
              </button>
            </div>
            <p v-if="pullStatusText" class="hint" :class="pullStatusClass">{{ pullStatusText }}</p>
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

        <section class="section">
          <button type="button" class="subsection-toggle" :aria-expanded="rawExpanded" @click="toggleRaw">
            <Icon name="chevron-down" :size="14" class="chevron" :class="{ open: rawExpanded }" />
            <h3>Advanced: Raw JSON</h3>
          </button>
          <p class="hint">
            The whole stored config document, secrets included <strong>unmasked</strong> — Synth has no
            auth, so this is a convenience, not a new exposure.
          </p>

          <template v-if="rawExpanded">
            <textarea
              v-model="rawJson"
              class="raw-json"
              spellcheck="false"
              aria-label="Raw config JSON"
            ></textarea>
            <div class="save-row">
              <button type="button" :disabled="rawStatus === 'saving'" @click="saveRaw">
                {{ rawStatus === 'saving' ? 'Saving…' : 'Save' }}
              </button>
              <span class="status" :class="rawStatus">
                <template v-if="rawStatus === 'saved'">Saved</template>
                <template v-else-if="rawStatus === 'error'">{{ rawError }}</template>
              </span>
            </div>
          </template>
        </section>
      </template>
    </div>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
}

.panel-heading {
  display: flex;
  align-items: center;
  gap: 8px;
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

.pull-button {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  cursor: pointer;
  border: 1px solid var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
  white-space: nowrap;
}

.pull-button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
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

.subsection-toggle {
  display: flex;
  align-items: center;
  gap: 6px;
  border: none;
  background: none;
  padding: 0;
  cursor: pointer;
  color: var(--text-h);
  margin-bottom: 8px;
}

.subsection-toggle h3 {
  margin: 0;
}

.subsection-toggle .chevron {
  transition: transform 0.15s;
  color: var(--text);
}

.subsection-toggle .chevron.open {
  transform: rotate(180deg);
}

.raw-json {
  width: 100%;
  min-height: 220px;
  font-family: var(--mono);
  font-size: 12px;
  padding: 10px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--code-bg);
  color: var(--text-h);
  resize: vertical;
  margin-bottom: 10px;
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
