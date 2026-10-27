<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import NoahTerminal from './noah/components/ui/NoahTerminal.vue'
import Icon from './Icon.vue'
import { launcherRepository, usePublicReleases, windowsX64Asset } from './release'
import linuxIcon from './icons/linux.svg'
import dockerIcon from './icons/docker.svg'

defineProps<{ content: Record<string, string>; labels: Record<string, string>; guideHref: string }>()
const selectedMode = ref<'script' | 'docker'>('script')
const modeIsDocker = computed(() => selectedMode.value === 'docker')
const tabs = ref<HTMLElement>()
// The version comes from GitHub at view time, so a new release needs no site rebuild.
const releases = usePublicReleases()
const latest = computed(() => releases.value?.[0])
// The launcher card turns into a download as soon as the launcher repository publishes a Windows x64 file.
const launcherReleases = usePublicReleases(launcherRepository)
const launcherRelease = computed(() => launcherReleases.value?.[0])
const launcherAsset = computed(() => windowsX64Asset(launcherRelease.value))
const launcherUrl = `https://github.com/${launcherRepository}`
const tabId = `install-${useId()}`
// Versionless asset names make the latest release's files stable addresses.
const releaseBase = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download'
const scriptCommand = 'curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash'
const dockerCommand = `${scriptCommand} -s -- --docker`
const windowsPath = 'M3 3h8.5v8.5H3Zm9.5 0H21v8.5h-8.5ZM3 12.5h8.5V21H3Zm9.5 0H21V21h-8.5Z'

async function changeTab(event: KeyboardEvent) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  selectedMode.value = event.key === 'Home' ? 'script' : event.key === 'End' ? 'docker' : modeIsDocker.value ? 'script' : 'docker'
  await nextTick()
  tabs.value?.querySelector<HTMLButtonElement>('[aria-selected="true"]')?.focus()
}
</script>

<template>
  <section id="download" class="download-section" aria-labelledby="download-title">
    <div class="download-heading">
      <h2 id="download-title">{{ content.title }}</h2>
      <p>{{ content.subtitle }}</p>
      <p v-if="latest" class="download-version">{{ content.latest }} <a :href="latest.url">{{ latest.tag }}</a> · <time :datetime="latest.date">{{ latest.date }}</time></p>
    </div>
    <div class="download-grid">
      <article v-for="architecture in ['x64', 'ARM64']" :key="architecture" class="download-card">
        <svg class="download-card-mark" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path :d="windowsPath" /></svg>
        <h3>Windows <span class="download-architecture">{{ architecture }}</span></h3>
        <p class="download-meta">{{ architecture === 'x64' ? content.windowsMeta : content.windowsArmMeta }}</p>
        <p class="download-note">{{ architecture === 'x64' ? content.windowsNote : content.windowsArmNote }}</p>
        <a class="download-button" :href="`${releaseBase}/bubblefighter-fantasy-win-${architecture.toLowerCase()}.zip`" :aria-label="`Windows ${architecture}: ${content.download}`"><Icon name="download" /><span>{{ content.download }}</span></a>
      </article>
    </div>
    <article class="download-card launcher-card" aria-labelledby="launcher-title">
      <svg class="download-card-mark" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path :d="windowsPath" /></svg>
      <div class="launcher-text">
        <h3 id="launcher-title">{{ content.launcherTitle }} <span class="download-architecture">Windows x64</span></h3>
        <p class="download-meta">{{ content.launcherMeta }}<template v-if="launcherAsset && launcherRelease"> · <a :href="launcherRelease.url">{{ launcherRelease.tag }}</a></template></p>
        <p class="download-note">{{ content.launcherNote }} <a :href="launcherUrl">{{ content.launcherRepo }}</a></p>
      </div>
      <a v-if="launcherAsset" class="download-button" :href="launcherAsset.url" :aria-label="`${content.launcherTitle} Windows x64: ${content.download}`"><Icon name="download" /><span>{{ content.launcherDownload }}</span></a>
      <span v-else class="download-button download-button-disabled" role="button" aria-disabled="true"><span>{{ content.launcherUnavailable }}</span></span>
    </article>
    <a class="download-guide" :href="guideHref">{{ content.guide }}<Icon name="arrow" /></a>
  </section>

  <section id="linux-install" class="install-panel" aria-labelledby="install-title">
    <h2 id="install-title" class="install-headline">
      <span>{{ content.installLine }}</span>
      <span>{{ content.startLine }}</span>
      <span class="install-headline-muted">{{ content.returnLine }}</span>
    </h2>
    <div class="install-controls">
      <div ref="tabs" class="install-tabs" role="tablist" :aria-label="content.installTitle" @keydown="changeTab">
        <button :id="`${tabId}-script`" type="button" role="tab" :aria-controls="`${tabId}-panel`" :aria-selected="!modeIsDocker" :tabindex="modeIsDocker ? -1 : 0" @click="selectedMode = 'script'"><img :src="linuxIcon" alt="" aria-hidden="true" />{{ content.scriptTab }}</button>
        <button :id="`${tabId}-docker`" type="button" role="tab" :aria-controls="`${tabId}-panel`" :aria-selected="modeIsDocker" :tabindex="modeIsDocker ? 0 : -1" @click="selectedMode = 'docker'"><img :src="dockerIcon" alt="" aria-hidden="true" />{{ content.dockerTab }}</button>
      </div>
      <div :id="`${tabId}-panel`" class="install-command-panel" role="tabpanel" :aria-labelledby="`${tabId}-${selectedMode}`" tabindex="0">
        <p class="install-description">{{ modeIsDocker ? content.dockerDescription : content.scriptDescription }}</p>
        <NoahTerminal :title="modeIsDocker ? 'Linux / Docker' : 'Linux / bash'" variant="glass" :controls="false" :labels="labels" :content="modeIsDocker ? dockerCommand : scriptCommand">
          <span class="install-comment"># {{ modeIsDocker ? content.dockerCommand : content.scriptCommand }}</span>{{ '\n' }}<span class="terminal-prompt">$</span> {{ modeIsDocker ? dockerCommand : scriptCommand }}
        </NoahTerminal>
      </div>
      <a class="install-guide" :href="guideHref">{{ content.guide }}<Icon name="arrow" /></a>
    </div>
  </section>
</template>
