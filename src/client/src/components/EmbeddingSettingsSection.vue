<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  getEmbeddingSettings,
  updateEmbeddingSettings,
  getOllamaModels,
  pullOllamaModel,
  getOllamaPullStatus,
  type EmbeddingSettings,
  type EmbeddingSettingsPatch,
  type OllamaPullStatus,
} from '../api'

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error'

const loaded = ref(false)
const loadError = ref('')

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

function apply(settings: EmbeddingSettings) {
  embedding.value = settings
  provider.value = settings.provider ?? ''
  ollamaEndpoint.value = settings.ollama.endpoint ?? ''
  ollamaModel.value = settings.ollama.model ?? ''
  openaiKey.value = ''
  openaiKeyClear.value = false
  openaiModel.value = settings.openai.model ?? ''
}

async function load() {
  loadError.value = ''
  try {
    apply(await getEmbeddingSettings())
    loaded.value = true
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  }
}

onMounted(() => {
  load() // apply() sets `provider`, which the watch below turns into a model fetch when Ollama
  pollPull() // resumes an in-flight or just-finished pull even after a reload
})

onUnmounted(stopPullPolling)

// Exposed so RawSettingsSection can ask this section to re-fetch after a raw-document save that
// may have changed the Embedding section too (SettingsPanel wires @saved to this).
defineExpose({ reload: load })

// Fetch the model list whenever the section becomes relevant — i.e. `provider` turns to Ollama, whether
// from the initial load (apply) or the user switching the dropdown. The section only renders
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
    apply(updated)
    embeddingStatus.value = 'saved'
    // The endpoint may have changed — refresh the model list against the just-saved Ollama instance.
    if (provider.value === 'Ollama') fetchOllamaModels()
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
  <section class="section">
    <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <template v-else-if="loaded">
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
    </template>
  </section>
</template>

<style scoped>
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
