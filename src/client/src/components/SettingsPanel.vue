<script setup lang="ts">
import { ref } from 'vue'
import Icon from './Icon.vue'
import VcsSettingsSection from './VcsSettingsSection.vue'
import EmbeddingSettingsSection from './EmbeddingSettingsSection.vue'
import RawSettingsSection from './RawSettingsSection.vue'

// Each section owns its own load/save state and fetches independently — a failure in one no
// longer blocks the others from rendering, unlike the single combined load gate this replaced.
const vcsSection = ref<InstanceType<typeof VcsSettingsSection> | null>(null)
const embeddingSection = ref<InstanceType<typeof EmbeddingSettingsSection> | null>(null)

// A raw-document save can touch the Vcs/Embedding sections underneath it, so ask them to
// re-fetch and reapply rather than going stale relative to what was just saved.
function onRawSaved() {
  vcsSection.value?.reload()
  embeddingSection.value?.reload()
}
</script>

<template>
  <section class="panel">
    <h2 class="panel-heading"><Icon name="sliders" :size="18" /> Settings</h2>

    <div class="body">
      <VcsSettingsSection ref="vcsSection" />
      <EmbeddingSettingsSection ref="embeddingSection" />
      <RawSettingsSection @saved="onRawSaved" />
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
</style>
