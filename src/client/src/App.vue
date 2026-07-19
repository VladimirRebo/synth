<script setup lang="ts">
import { nextTick, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSearchFocus } from './composables/useSearchFocus'
import Sidebar from './components/Sidebar.vue'

const { focus } = useSearchFocus()
const route = useRoute()
const router = useRouter()

// Global Cmd/Ctrl+K jumps to Search from anywhere and focuses its input — previously this
// worked because SearchPanel was always mounted on the single page; now that each panel is
// its own route, get there first (SearchPanel needs to actually mount before its input ref
// exists) and only then focus.
async function onGlobalKeydown(e: KeyboardEvent) {
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault()
    if (route.name !== 'search') {
      await router.push({ name: 'search' })
      await nextTick()
    }
    focus()
  }
}

onMounted(() => document.addEventListener('keydown', onGlobalKeydown))
onUnmounted(() => document.removeEventListener('keydown', onGlobalKeydown))
</script>

<template>
  <div class="app">
    <Sidebar />
    <main class="content">
      <router-view />
    </main>
  </div>
</template>

<style scoped>
.app {
  display: flex;
  align-items: flex-start;
  min-height: 100vh;
}

.content {
  flex: 1;
  min-width: 0;
  max-width: 1100px;
  margin: 0 auto;
  padding: 24px 32px 48px;
}
</style>
