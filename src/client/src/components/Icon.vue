<script setup lang="ts">
import { computed } from 'vue'

// Zero-dependency inline-SVG icon set, adapted from Sonar's Icon.vue pattern: one shared
// 24x24 viewBox, stroke-based (currentColor), a small registry keyed by name. Each entry can
// mix simple primitives (paths/circles/lines/polylines) — just enough to draw the handful of
// icons this app actually uses, no icon-font/library dependency.
interface IconDef {
  paths?: string[]
  circles?: { cx: number; cy: number; r: number }[]
  lines?: { x1: number; y1: number; x2: number; y2: number }[]
  polylines?: string[]
  rects?: { x: number; y: number; width: number; height: number; rx?: number }[]
}

const icons: Record<string, IconDef> = {
  search: {
    circles: [{ cx: 11, cy: 11, r: 8 }],
    lines: [{ x1: 21, y1: 21, x2: 16.65, y2: 16.65 }],
  },
  clock: {
    circles: [{ cx: 12, cy: 12, r: 10 }],
    polylines: ['12 6 12 12 16 14'],
  },
  x: {
    lines: [
      { x1: 18, y1: 6, x2: 6, y2: 18 },
      { x1: 6, y1: 6, x2: 18, y2: 18 },
    ],
  },
  copy: {
    rects: [{ x: 9, y: 9, width: 13, height: 13, rx: 2 }],
    paths: ['M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'],
  },
  'chevron-down': {
    polylines: ['6 9 12 15 18 9'],
  },
  sun: {
    circles: [{ cx: 12, cy: 12, r: 5 }],
    lines: [
      { x1: 12, y1: 1, x2: 12, y2: 3 },
      { x1: 12, y1: 21, x2: 12, y2: 23 },
      { x1: 4.22, y1: 4.22, x2: 5.64, y2: 5.64 },
      { x1: 18.36, y1: 18.36, x2: 19.78, y2: 19.78 },
      { x1: 1, y1: 12, x2: 3, y2: 12 },
      { x1: 21, y1: 12, x2: 23, y2: 12 },
      { x1: 4.22, y1: 19.78, x2: 5.64, y2: 18.36 },
      { x1: 18.36, y1: 5.64, x2: 19.78, y2: 4.22 },
    ],
  },
  moon: {
    paths: ['M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'],
  },
  plug: {
    // Two prongs feeding into a socket body, wired to a dot — a simple original glyph
    // (feather-icons has no "plug"), used for the MCP connect panel.
    lines: [
      { x1: 9, y1: 2, x2: 9, y2: 6 },
      { x1: 15, y1: 2, x2: 15, y2: 6 },
      { x1: 12, y1: 17, x2: 12, y2: 22 },
    ],
    rects: [{ x: 6, y: 6, width: 12, height: 8, rx: 2 }],
  },
  folder: {
    paths: [
      'M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z',
    ],
  },
  sliders: {
    // feather-icons' sliders glyph — used for the Settings panel.
    lines: [
      { x1: 4, y1: 21, x2: 4, y2: 14 },
      { x1: 4, y1: 10, x2: 4, y2: 3 },
      { x1: 12, y1: 21, x2: 12, y2: 12 },
      { x1: 12, y1: 8, x2: 12, y2: 3 },
      { x1: 20, y1: 21, x2: 20, y2: 16 },
      { x1: 20, y1: 12, x2: 20, y2: 3 },
      { x1: 1, y1: 14, x2: 7, y2: 14 },
      { x1: 9, y1: 8, x2: 15, y2: 8 },
      { x1: 17, y1: 16, x2: 23, y2: 16 },
    ],
  },
  'git-branch': {
    // feather-icons' git-branch glyph — used for the remote-repository indexing mode.
    lines: [
      { x1: 6, y1: 3, x2: 6, y2: 15 },
    ],
    circles: [
      { cx: 18, cy: 6, r: 3 },
      { cx: 6, cy: 18, r: 3 },
    ],
    paths: ['M18 9a9 9 0 0 1-9 9'],
  },
}

const props = withDefaults(defineProps<{ name: string; size?: number }>(), { size: 16 })

const def = computed(() => icons[props.name] ?? {})
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path v-for="(d, i) in def.paths" :key="`p${i}`" :d="d" />
    <circle v-for="(c, i) in def.circles" :key="`c${i}`" :cx="c.cx" :cy="c.cy" :r="c.r" />
    <line v-for="(l, i) in def.lines" :key="`l${i}`" :x1="l.x1" :y1="l.y1" :x2="l.x2" :y2="l.y2" />
    <polyline v-for="(pts, i) in def.polylines" :key="`pl${i}`" :points="pts" />
    <rect
      v-for="(r, i) in def.rects"
      :key="`r${i}`"
      :x="r.x"
      :y="r.y"
      :width="r.width"
      :height="r.height"
      :rx="r.rx ?? 0"
    />
  </svg>
</template>
