<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getRawSettings, updateRawSettings } from '../api'
import Icon from './Icon.vue'

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error'

const emit = defineEmits<{ saved: [] }>()

const loadError = ref('')
const rawExpanded = ref(false)
const rawJson = ref('')
const rawStatus = ref<SaveStatus>('idle')
const rawError = ref('')

function apply(json: string) {
  try {
    rawJson.value = JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    rawJson.value = json
  }
}

onMounted(async () => {
  // Fetched eagerly (not deferred to first expand) so an expand never has to wait on a request.
  try {
    apply(await getRawSettings())
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  }
})

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
    apply(await updateRawSettings(rawJson.value))
    rawStatus.value = 'saved'
    // The raw document may have changed the Vcs/Embedding sections too — let the parent refresh
    // their structured views so they don't go stale relative to what was just saved.
    emit('saved')
  } catch (err) {
    rawError.value = err instanceof Error ? err.message : String(err)
    rawStatus.value = 'error'
  }
}
</script>

<template>
  <section class="section">
    <button type="button" class="subsection-toggle" :aria-expanded="rawExpanded" @click="toggleRaw">
      <Icon name="chevron-down" :size="14" class="chevron" :class="{ open: rawExpanded }" />
      <h3>Advanced: Raw JSON</h3>
    </button>
    <p class="hint">
      The whole stored config document, secrets included <strong>unmasked</strong> — Synth has no
      auth, so this is a convenience, not a new exposure.
    </p>
    <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

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

<style scoped>
.section h3 {
  margin: 0 0 12px;
  font-size: 14px;
  color: var(--text);
  text-transform: uppercase;
  letter-spacing: 0.04em;
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
