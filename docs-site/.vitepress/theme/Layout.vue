<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { Content, useData, useRoute, withBase } from 'vitepress'
import VPNavBarSearch from 'vitepress/dist/client/theme-default/components/VPNavBarSearch.vue'
import Icon from './Icon.vue'
import LiquidGlassLink from './LiquidGlassLink.vue'
import HeaderDropdown from './HeaderDropdown.vue'
import DownloadSection from './DownloadSection.vue'
import NoahButton from './noah/components/ui/NoahButton.vue'
import NoahReveal from './noah/components/ui/NoahReveal.vue'
import NoahTerminal from './noah/components/ui/NoahTerminal.vue'
import { languages, labels, routes } from './locales'

const { lang, frontmatter, isDark, page } = useData()
const route = useRoute()
const repositoryUrl = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy'
const terminalCommand = 'curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash\n~/bubblefighter-fantasy/bff'
const language = computed(() => languages.find(item => item.code === lang.value) || languages[0])
const text = computed(() => labels[language.value.key])
const home = computed(() => frontmatter.value.layout === 'home')
const mobileOpen = ref(false)
const backgroundVideo = ref<HTMLVideoElement>()
const videoReady = ref(false)
const allowMotion = ref(false)
let motionMedia: MediaQueryList
function updateMotion() {
  allowMotion.value = !motionMedia.matches
  if (!allowMotion.value) backgroundVideo.value?.pause()
}
function updateVideoPlayback() {
  if (!backgroundVideo.value) return
  if (document.hidden || !allowMotion.value) backgroundVideo.value.pause()
  else backgroundVideo.value.play().catch(() => {})
}
const sidebar = ref<HTMLElement>()
const menuButton = ref<HTMLButtonElement>()
const viewportIsMobile = ref(false)
let previousFocus: HTMLElement | null = null
let media: MediaQueryList
function updateViewport() {
  viewportIsMobile.value = media.matches
  if (!media.matches) mobileOpen.value = false
}
const openHeaderMenu = ref<string | null>(null)
function setHeaderMenu(id: string, open: boolean) {
  if (open) openHeaderMenu.value = id
  else if (openHeaderMenu.value === id) openHeaderMenu.value = null
}
const outline = ref<{ id: string; text: string; level: number }[]>([])
const activeHeading = ref('overview')
const currentPath = computed(() => page.value.relativePath.replace(/^(zh-tw|ko|en)\//, '').replace(/\.md$/, ''))
const currentIndex = computed(() => routes.indexOf(currentPath.value))
const groups = computed(() => [
  { title: text.value.start, items: [0, 1] },
  { title: text.value.guides, items: [2, 3, 4, 5, 6, 7] },
  { title: text.value.project, items: [8, 9] },
])
const link = (path = '', prefix = language.value.prefix) => withBase(`/${prefix}${path === 'index' ? '' : path}`)
const closeMenus = () => { mobileOpen.value = false; openHeaderMenu.value = null }
function scrollSpy() {
  let id = 'overview'
  for (const heading of outline.value) {
    if ((document.getElementById(heading.id)?.getBoundingClientRect().top ?? Infinity) <= 130) id = heading.id
  }
  activeHeading.value = id
}
async function updateOutline() {
  await nextTick()
  outline.value = Array.from(document.querySelectorAll<HTMLElement>('.article-body h2, .article-body h3'))
    .filter(node => node.id)
    .map(node => ({ id: node.id, text: node.textContent?.replace(/\s*#\s*$/, '').trim() || '', level: Number(node.tagName[1]) }))
  scrollSpy()
}
function keydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeMenus()
  }
  if (event.key !== 'Tab' || !mobileOpen.value) return
  const focusable = Array.from(sidebar.value?.querySelectorAll<HTMLElement>('a, summary, button') || [])
    .filter(element => element.getClientRects().length)
  const first = focusable[0], last = focusable.at(-1)
  if (!first || !last) return
  if (event.shiftKey && (document.activeElement === first || !sidebar.value?.contains(document.activeElement))) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
}
watch(() => route.path, () => { closeMenus(); updateOutline() })
watch(mobileOpen, async value => {
  if (value) openHeaderMenu.value = null
  document.documentElement.classList.toggle('menu-open', value)
  if (value) {
    previousFocus = document.activeElement as HTMLElement
    await nextTick()
    sidebar.value?.querySelector<HTMLElement>('button, summary, a')?.focus()
  } else if (previousFocus?.isConnected) previousFocus.focus()
})
onMounted(() => { media = window.matchMedia('(max-width: 799px)'); updateViewport(); media.addEventListener('change', updateViewport); motionMedia = window.matchMedia('(prefers-reduced-motion: reduce)'); updateMotion(); motionMedia.addEventListener('change', updateMotion); document.addEventListener('visibilitychange', updateVideoPlayback); updateOutline(); window.addEventListener('scroll', scrollSpy, { passive: true }); window.addEventListener('keydown', keydown) })
onUnmounted(() => { media?.removeEventListener('change', updateViewport); motionMedia?.removeEventListener('change', updateMotion); document.removeEventListener('visibilitychange', updateVideoPlayback); window.removeEventListener('scroll', scrollSpy); window.removeEventListener('keydown', keydown); document.documentElement.classList.remove('menu-open') })
</script>

<template>
  <a class="skip-link" href="#main-content">{{ text.skip }}</a>
  <header class="topbar">
    <div class="topbar-inner glass-pill">
      <a :href="link()" class="brand" aria-label="Bubble Fighter Fantasy">
        <span class="brand-name">Bubble Fighter Fantasy</span><span class="brand-docs"> / {{ text.docs }}</span>
      </a>
      <nav class="header-nav" :aria-label="text.docs">
        <HeaderDropdown v-for="(group, groupIndex) in groups" :key="groupIndex" class="nav-group" :label="group.title" :open="openHeaderMenu === `nav-${groupIndex}`" @update:open="setHeaderMenu(`nav-${groupIndex}`, $event)">
          <template #trigger>{{ group.title }}<Icon name="chevron" /></template>
          <a v-for="index in group.items" :key="index" :href="link(routes[index])">{{ text.pages[index] }}</a>
        </HeaderDropdown>
        <a class="header-download" :href="`${link()}#download`" @pointerenter="openHeaderMenu = null">{{ text.download.nav }}</a>
      </nav>
      <div class="header-search-tools">
        <a class="github-link" :href="repositoryUrl" target="_blank" rel="noopener noreferrer" aria-label="GitHub" title="GitHub" @pointerenter="openHeaderMenu = null"><Icon name="github" /></a>
        <VPNavBarSearch />
      </div>
      <div class="preferences">
        <NoahButton variant="ghost" class="round-button theme-toggle" :aria-label="text.theme" @click="isDark = !isDark"><Icon :name="isDark ? 'moon' : 'sun'" /></NoahButton>
        <HeaderDropdown class="language-select" :label="text.languages" align="right" :hover="false" :open="openHeaderMenu === 'language'" @update:open="setHeaderMenu('language', $event)">
          <template #trigger><Icon name="language" /><span class="language-label">{{ language.label }}</span><Icon name="chevron" /></template>
          <a v-for="item in languages" :key="item.key" :href="link(currentPath, item.prefix)" :lang="item.code" :aria-current="item.key === language.key ? 'true' : undefined">{{ item.label }}<span v-if="item.key === language.key" aria-hidden="true">✓</span></a>
        </HeaderDropdown>
        <button ref="menuButton" class="round-button mobile-toggle" :aria-label="mobileOpen ? text.close : text.menu" :aria-expanded="mobileOpen" aria-controls="site-sidebar" @click="mobileOpen = !mobileOpen"><Icon :name="mobileOpen ? 'close' : 'menu'" /></button>
      </div>
    </div>
  </header>

  <button v-if="mobileOpen" class="sidebar-backdrop" :aria-label="text.close" @click="closeMenus" />
  <aside v-if="!home || mobileOpen" id="site-sidebar" ref="sidebar" class="sidebar glass-rail" :inert="viewportIsMobile && !mobileOpen" :role="mobileOpen ? 'dialog' : undefined" :aria-modal="mobileOpen ? 'true' : undefined" :aria-label="text.menu" :class="{ 'is-open': mobileOpen }">
    <button v-if="mobileOpen" class="drawer-close" @click="closeMenus"><Icon name="close" />{{ text.close }}</button>
    <nav :aria-label="text.docs">
      <details v-for="group in groups" :key="group.title" open class="sidebar-group">
        <summary>{{ group.title }}<Icon name="chevron" /></summary>
        <a v-for="index in group.items" :key="index" :href="link(routes[index])" :aria-current="currentIndex === index ? 'page' : undefined">{{ text.pages[index] }}</a>
      </details>
    </nav>
    <a class="sidebar-download" :href="`${link()}#download`" @click="closeMenus">{{ text.download.nav }}<Icon name="download" /></a>
    <a class="sidebar-github" :href="repositoryUrl" target="_blank" rel="noopener noreferrer">GitHub<Icon name="github" /></a>
    <a class="sidebar-home" :href="link()">{{ text.home }}<Icon name="arrow" /></a>
  </aside>

  <main v-if="home" id="main-content" class="landing" :inert="mobileOpen">
    <div class="landing-video" :class="{ 'is-ready': videoReady }" aria-hidden="true">
      <video v-if="allowMotion" ref="backgroundVideo" :src="withBase('/media/boom-hill-background.mp4')" autoplay muted loop playsinline preload="metadata" @loadeddata="videoReady = true" @error="videoReady = false" />
    </div>
    <section class="hero">

      <NoahReveal as="h1" :delay="80">{{ text.hero }}</NoahReveal><NoahReveal as="p" :delay="150">{{ text.intro }}</NoahReveal>
      <NoahReveal class="hero-actions" :delay="220"><a class="primary-action" :href="link(routes[0])">{{ text.action }}<Icon name="arrow" /></a><LiquidGlassLink :href="link(routes[3])">{{ text.secondary }}<Icon name="chevron" /></LiquidGlassLink></NoahReveal>
      <NoahReveal class="implementation" :delay="300"><span>{{ text.implementation }}</span><strong>C# / .NET 10</strong></NoahReveal>
      <NoahReveal class="sdk-options" :delay="350"><span>{{ text.sdk }}</span><div class="language-chips"><span>Python</span><span>TypeScript</span><span>Go</span><span>Rust</span></div></NoahReveal>
    </section>
    <NoahReveal as="section" class="landing-terminal" :delay="400" :aria-label="text.pages[1]">
      <NoahTerminal title="Bubble Fighter Fantasy" variant="glass" controls :labels="text.terminal" :content="terminalCommand">
        <span class="terminal-comment"># {{ text.run.linux }}</span>{{ '\n' }}<span class="terminal-prompt">$</span> curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash{{ '\n' }}<span class="terminal-success">==&gt; Bubble Fighter Fantasy is installed.</span>{{ '\n\n' }}<span class="terminal-comment"># {{ text.run.start }}</span>{{ '\n' }}<span class="terminal-prompt">$</span> ~/bubblefighter-fantasy/bff{{ '\n' }}<span class="terminal-success">WebUI: http://127.0.0.1:8080/</span>{{ '\n\n' }}<span class="terminal-comment"># {{ text.run.windows }}</span>{{ '\n\n' }}<span class="terminal-comment">{{ text.footer }}</span><span class="cursor" />
      </NoahTerminal>
    </NoahReveal>
    <DownloadSection :content="text.download" :labels="text.terminal" :guide-href="link(routes[0])" />
    <section class="landing-cards"><NoahReveal as="a" :delay="index * 90" v-for="(title, index) in text.cards" :key="title" :href="link(routes[[0, 2, 3][index]])"><Icon :name="['book', 'settings', 'layers'][index]"/><h2>{{ title }}</h2><p>{{ text.descriptions[index] }}</p><Icon class="card-arrow" name="arrow" /></NoahReveal></section>
    <Content class="home-content" />
    <footer class="site-footer"><span>Bubble Fighter Fantasy</span><span>© 2026 Fantasy Tale Studio</span><span>MIT</span></footer>
  </main>

  <div v-else class="document-frame" :inert="mobileOpen">
    <main id="main-content" class="document">
      <div class="mobile-breadcrumb"><button @click="mobileOpen = true"><Icon name="menu" />{{ text.menu }}</button><span>{{ page.title }}</span></div>
      <div id="overview" class="document-heading"><h1>{{ page.title }}</h1></div>
      <Content class="article-body vp-doc" />
      <footer class="document-footer">
        <p>{{ text.updated }} · {{ frontmatter.updated || '2026-10-15' }}</p>
        <div class="page-neighbours">
          <a v-if="currentIndex > 0" :href="link(routes[currentIndex - 1])"><small>{{ text.previous }}</small><strong>← {{ text.pages[currentIndex - 1] }}</strong></a><span v-else />
          <a v-if="currentIndex >= 0 && currentIndex < routes.length - 1" class="next-page" :href="link(routes[currentIndex + 1])"><small>{{ text.next }}</small><strong>{{ text.pages[currentIndex + 1] }} →</strong></a>
        </div>
      </footer>
    </main>
    <aside class="outline"><nav :aria-label="text.outline"><h2>{{ text.outline }}</h2><a href="#overview" :aria-current="activeHeading === 'overview' ? 'location' : undefined">{{ text.overview }}</a><a v-for="heading in outline" :key="heading.id" :href="`#${heading.id}`" :class="{ nested: heading.level === 3 }" :aria-current="activeHeading === heading.id ? 'location' : undefined">{{ heading.text }}</a></nav></aside>
  </div>
</template>
