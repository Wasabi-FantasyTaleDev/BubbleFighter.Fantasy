<script setup lang="ts">
defineProps<{ name: string }>()
const paths: Record<string, string> = {
  github: 'M12 .3a12 12 0 0 0-3.8 23.38c.6.12.83-.26.83-.57L9 21.07c-3.34.72-4.04-1.61-4.04-1.61-.55-1.39-1.34-1.76-1.34-1.76-1.08-.74.09-.73.09-.73 1.2.09 1.83 1.24 1.83 1.24 1.08 1.83 2.81 1.3 3.5 1 .1-.78.42-1.31.76-1.61-2.67-.3-5.47-1.33-5.47-5.93 0-1.31.47-2.38 1.24-3.22-.14-.3-.54-1.52.1-3.18 0 0 1-.32 3.3 1.23a11.5 11.5 0 0 1 6 0c2.28-1.55 3.29-1.23 3.29-1.23.64 1.66.24 2.88.12 3.18a4.65 4.65 0 0 1 1.23 3.22c0 4.61-2.8 5.63-5.48 5.92.42.36.81 1.1.81 2.22l-.01 3.29c0 .31.2.69.82.57A12 12 0 0 0 12 .3Z',
  sun: 'M12 3v2m0 14v2M3 12h2m14 0h2M5.6 5.6 7 7m10 10 1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  moon: 'M20 14.1A8 8 0 0 1 9.9 4a8.1 8.1 0 1 0 10.2 10.2',
  language: 'M3 5h12M9 3v2m4 0c-1 6-4 9-9 11m1-8c2 4 4 6 8 8m1 5 4-10 4 10m-6.5-3h5',
  chevron: 'm8 10 4 4 4-4',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'm6 6 12 12M6 18 18 6',
  arrow: 'M4 12h15m-6-6 6 6-6 6',
  download: 'M12 3v12m-5-5 5 5 5-5M4 16v4h16v-4',
  layers: 'm12 3 10 5-10 5L2 8l10-5ZM2 12l10 5 10-5M2 16l10 5 10-5',
  settings: 'M4 7h16M4 17h16M8 4v6m8 4v6',
  book: 'M12 6c-3-2-6-2-9-1v15c3-1 6-1 9 1m0-15c3-2 6-2 9-1v15c-3-1-6-1-9 1V6',
}
</script>
<template><svg viewBox="0 0 24 24" :fill="name === 'github' ? 'currentColor' : 'none'" :stroke="name === 'github' ? 'none' : 'currentColor'" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path :d="paths[name] || paths.arrow" /></svg></template>
