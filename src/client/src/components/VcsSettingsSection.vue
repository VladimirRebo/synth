<script setup lang="ts">
import { onMounted, ref } from 'vue'
import {
  getVcsSettings,
  updateVcsSettings,
  pollRepositoriesNow,
  type VcsSettings,
  type VcsSettingsPatch,
} from '../api'

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error'

const loaded = ref(false)
const loadError = ref('')

const vcs = ref<VcsSettings | null>(null)
const workspaceRoot = ref('')
const pollIntervalMinutes = ref(5)
const githubToken = ref('')
const githubClear = ref(false)
const gitlabToken = ref('')
const gitlabClear = ref(false)
const vcsStatus = ref<SaveStatus>('idle')
const vcsError = ref('')

const pollNowStatus = ref<'idle' | 'checking' | 'done' | 'error'>('idle')
const pollNowMessage = ref('')

function apply(settings: VcsSettings) {
  vcs.value = settings
  workspaceRoot.value = settings.workspaceRoot ?? ''
  pollIntervalMinutes.value = settings.pollIntervalMinutes
  githubToken.value = ''
  githubClear.value = false
  gitlabToken.value = ''
  gitlabClear.value = false
}

async function load() {
  loadError.value = ''
  try {
    apply(await getVcsSettings())
    loaded.value = true
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  }
}

onMounted(load)

// Exposed so RawSettingsSection can ask this section to re-fetch after a raw-document save that
// may have changed the Vcs section too (SettingsPanel wires @saved to this).
defineExpose({ reload: load })

async function saveVcs() {
  if (!vcs.value) return

  const patch: VcsSettingsPatch = {}
  if (workspaceRoot.value.trim() !== (vcs.value.workspaceRoot ?? '')) {
    patch.workspaceRoot = workspaceRoot.value.trim() || null
  }
  if (pollIntervalMinutes.value !== vcs.value.pollIntervalMinutes) {
    patch.pollIntervalMinutes = pollIntervalMinutes.value
  }
  if (githubClear.value) patch.github = { token: '' }
  else if (githubToken.value) patch.github = { token: githubToken.value }
  if (gitlabClear.value) patch.gitlab = { token: '' }
  else if (gitlabToken.value) patch.gitlab = { token: gitlabToken.value }

  vcsStatus.value = 'saving'
  vcsError.value = ''
  try {
    const updated = await updateVcsSettings(patch)
    apply(updated)
    vcsStatus.value = 'saved'
  } catch (err) {
    vcsError.value = err instanceof Error ? err.message : String(err)
    vcsStatus.value = 'error'
  }
}

async function pollNow() {
  pollNowStatus.value = 'checking'
  pollNowMessage.value = ''
  try {
    const { triggered } = await pollRepositoriesNow()
    pollNowStatus.value = 'done'
    pollNowMessage.value =
      triggered === 0
        ? 'No changes found.'
        : `Reindexing ${triggered} ${triggered === 1 ? 'repository' : 'repositories'}.`
  } catch (err) {
    pollNowStatus.value = 'error'
    pollNowMessage.value = err instanceof Error ? err.message : String(err)
  }
}
</script>

<template>
  <section class="section">
    <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <template v-else-if="loaded">
      <h3>VCS</h3>
      <div class="field">
        <label>Workspace root</label>
        <input v-model="workspaceRoot" type="text" placeholder="~/.synth/workspaces (default)" />
      </div>
      <div class="field">
        <label>Poll interval (minutes)</label>
        <input v-model.number="pollIntervalMinutes" type="number" min="0" step="1" />
        <p class="hint">
          How often each indexed repository is checked for a new commit and reindexed if one is
          found. 0 disables polling.
        </p>
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
      <div class="save-row">
        <button type="button" :disabled="pollNowStatus === 'checking'" @click="pollNow">
          {{ pollNowStatus === 'checking' ? 'Checking…' : 'Check for updates now' }}
        </button>
        <span class="status" :class="pollNowStatus === 'error' ? 'error' : 'saved'">
          <template v-if="pollNowStatus === 'done' || pollNowStatus === 'error'">{{ pollNowMessage }}</template>
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
