<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import NoahTerminal from './noah/components/ui/NoahTerminal.vue'
import Icon from './Icon.vue'
import { launcherRepository, usePublicReleases, windowsX64Asset } from './release'
import linuxIcon from './icons/linux.svg'
import dockerIcon from './icons/docker.svg'

const props = defineProps<{ content: Record<string, string>; labels: Record<string, string>; guideHref: string; tutorialHref: string }>()
type Mode = 'script' | 'docker' | 'windows'
const modes: Mode[] = ['script', 'docker', 'windows']
const selectedMode = ref<Mode>('script')
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
const scriptCommand = 'curl -fsSL https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.sh | bash'
// Same options as the tutorial's Commands view: root installs Docker when it is missing and adds a database container.
const dockerCommand = 'curl -fsSL https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.sh | sudo bash -s -- --docker --install-docker --with-mysql'
// Same script and options as the tutorial's Commands view and the installation guide.
const windowsCommand = 'irm https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1 | iex'
const commands = computed(() => ({
  script: { title: 'Linux / bash', prompt: '$', command: scriptCommand, description: props.content.scriptDescription, comment: props.content.scriptCommand },
  docker: { title: 'Linux / Docker', prompt: '$', command: dockerCommand, description: props.content.dockerDescription, comment: props.content.dockerCommand },
  windows: { title: 'Windows / PowerShell', prompt: 'PS>', command: windowsCommand, description: props.content.windowsDescription, comment: props.content.windowsCommand },
}[selectedMode.value]))
const windowsPath = 'M3 3h8.5v8.5H3Zm9.5 0H21v8.5h-8.5ZM3 12.5h8.5V21H3Zm9.5 0H21V21h-8.5Z'

async function changeTab(event: KeyboardEvent) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const n = modes.indexOf(selectedMode.value)
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? modes.length - 1 : (n + (event.key === 'ArrowRight' ? 1 : modes.length - 1)) % modes.length
  selectedMode.value = modes[next]
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
        <button v-for="mode in modes" :id="`${tabId}-${mode}`" :key="mode" type="button" role="tab" :aria-controls="`${tabId}-panel`" :aria-selected="selectedMode === mode" :tabindex="selectedMode === mode ? 0 : -1" @click="selectedMode = mode">
          <svg v-if="mode === 'windows'" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path :d="windowsPath" /></svg>
          <img v-else :src="mode === 'docker' ? dockerIcon : linuxIcon" alt="" aria-hidden="true" />{{ mode === 'script' ? content.scriptTab : mode === 'docker' ? content.dockerTab : content.windowsTab }}
        </button>
      </div>
      <div :id="`${tabId}-panel`" class="install-command-panel" role="tabpanel" :aria-labelledby="`${tabId}-${selectedMode}`" tabindex="0">
        <p class="install-description">{{ commands.description }}</p>
        <NoahTerminal :title="commands.title" variant="glass" :controls="false" :labels="labels" :content="commands.command">
          <span class="install-comment"># {{ commands.comment }}</span>{{ '\n' }}<span class="terminal-prompt">{{ commands.prompt }}</span> {{ commands.command }}
        </NoahTerminal>
      </div>
      <!-- The panel under the command opens the install-script tutorial, which plays the same commands step by step. -->
      <a class="install-guide" :href="selectedMode === 'windows' ? `${tutorialHref}#windows` : tutorialHref">{{ content.tutorial }}<Icon name="arrow" /></a>
    </div>
  </section>
</template>
