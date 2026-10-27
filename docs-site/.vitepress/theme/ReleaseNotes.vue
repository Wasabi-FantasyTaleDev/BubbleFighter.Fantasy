<script setup lang="ts">
import { serverRepository, usePublicReleases } from './release'

defineProps<{ content: Record<string, string> }>()
const releases = usePublicReleases()
const releasesPage = `https://github.com/${serverRepository}/releases`
</script>

<template>
  <!-- Live release notes from the public repository; hidden when GitHub cannot be reached. -->
  <section v-if="releases && releases.length" class="release-notes" aria-labelledby="release-notes-title">
    <h2 id="release-notes-title">{{ content.title }}</h2>
    <p class="release-notes-intro">{{ content.intro }}</p>
    <article v-for="(release, index) in releases.slice(0, 5)" :key="release.tag" class="release-entry">
      <header>
        <h3><a :href="release.url">{{ release.name }}</a></h3>
        <span class="release-tag">{{ release.tag }}<template v-if="index === 0"> · {{ content.latest }}</template></span>
        <time :datetime="release.date">{{ release.date }}</time>
      </header>
      <div class="release-body vp-doc" v-html="release.html" />
    </article>
    <a class="release-all" :href="releasesPage">{{ content.all }}</a>
  </section>
</template>
