<script setup>
import odteamMark from './assets/icon_odteam.png'
import { computed, ref, watch, watchEffect, onMounted, onBeforeUnmount, nextTick } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import NoahIcon from './noah/components/icons/NoahIcon.vue'
import NoahButton from './noah/components/ui/NoahButton.vue'
import NoahInput from './noah/components/ui/NoahInput.vue'
import SignInView from './views/SignInView.vue'
import SetupView from './views/SetupView.vue'
import WebUiUpdateNotice from './components/WebUiUpdateNotice.vue'
import GithubMirrorSuggestion from './components/GithubMirrorSuggestion.vue'
import NoahToastContainer from './noah/components/ui/NoahToastContainer.vue'
import { PAGES, STORE_PAGES } from './router'
import { useUiStore } from './stores/ui'
import { useServerStore } from './stores/server'
import { useI18n } from './i18n'

const route = useRoute()
const ui = useUiStore()
const server = useServerStore()
const { t } = useI18n()
const navSearch = ref('')
const collapsed = ref(false)
const storeExpanded = ref(false)
const overviewExpanded = ref(false)
const mobile = ref(false)
const sidebar = ref(null)
const menuButton = ref(null)
const availableStorePages = computed(() => STORE_PAGES.filter(page => !page.installation || server.installation))
const storePage = computed(() => availableStorePages.value.find(page => page.id === route.query.view) || STORE_PAGES[0])
const matches = text => t(text).toLocaleLowerCase().includes(navSearch.value.trim().toLocaleLowerCase())
const exactSearchMatch = text => t(text).toLocaleLowerCase() === navSearch.value.trim().toLocaleLowerCase()
const overviewPages = computed(() => PAGES.filter(page => page.parent === 'overview'))
const filteredOverviewPages = computed(() => overviewPages.value.filter(child => !navSearch.value.trim() || exactSearchMatch('nav.overview') || exactSearchMatch('nav.group.server') || matches(child.menuTitle || child.title)))
const filteredStorePages = computed(() => availableStorePages.value.filter(child => !navSearch.value.trim() || exactSearchMatch('nav.store') || exactSearchMatch('nav.group.system') || matches(child.title)))
const overviewMenuOpen = computed(() => overviewExpanded.value || !!navSearch.value.trim())
const groups = computed(() => ['server', 'system'].map(id => ({
  id, title: 'nav.group.' + id,
  pages: PAGES.filter(page => !page.parent && page.path !== 'settings' && page.group === id && (matches(page.title) || matches('nav.group.' + id) ||
    page.path === 'overview' && overviewPages.value.some(child => matches(child.menuTitle || child.title)) ||
    page.path === 'store' && availableStorePages.value.some(child => matches(child.title))))
})).filter(group => group.pages.length))
const breadcrumbGroup = computed(() => 'nav.group.' + (route.meta.group || 'system'))
const groupTarget = computed(() => route.meta.group === 'server' ? '/overview' : '/plugins')
const storeMenuOpen = computed(() => storeExpanded.value || !!navSearch.value.trim())

function toggleStoreMenu() {
  if (collapsed.value && !mobile.value) { collapsed.value = false; storeExpanded.value = true }
  else { navSearch.value = ''; storeExpanded.value = !storeExpanded.value }
}
function toggleOverviewMenu() {
  if (collapsed.value && !mobile.value) { collapsed.value = false; overviewExpanded.value = true }
  else { navSearch.value = ''; overviewExpanded.value = !overviewExpanded.value }
}
function openOverviewMenu() {
  navSearch.value = ''
  overviewExpanded.value = true
  if (!mobile.value) collapsed.value = false
}

function closeSidebar() {
  ui.sideOpen = false
  nextTick(() => menuButton.value?.$el.focus())
}
function sidebarKeydown(event) {
  if (!mobile.value || !ui.sideOpen) return
  if (event.key === 'Escape') { event.preventDefault(); closeSidebar(); return }
  if (event.key !== 'Tab') return
  const controls = [...sidebar.value.querySelectorAll('a[href], button:not(:disabled), input:not(:disabled)')]
    .filter(element => element.getClientRects().length)
  const first = controls[0], last = controls.at(-1)
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
}
let viewport
const updateViewport = () => { mobile.value = viewport.matches; if (!viewport.matches) ui.sideOpen = false }
onMounted(() => {
  server.initialize()
  viewport = matchMedia('(max-width: 860px)')
  updateViewport()
  viewport.addEventListener('change', updateViewport)
})
onBeforeUnmount(() => viewport?.removeEventListener('change', updateViewport))
watch(() => ui.sideOpen, async open => {
  if (open && mobile.value) { await nextTick(); sidebar.value?.querySelector('button')?.focus() }
})
watch(collapsed, value => { if (value) navSearch.value = '' })
watchEffect(onCleanup => {
  if (!mobile.value || !ui.sideOpen) return
  const previous = document.body.style.overflow
  document.body.style.overflow = 'hidden'
  onCleanup(() => { document.body.style.overflow = previous })
})
watch(() => route.fullPath, () => { ui.sideOpen = false })
watch(() => route.name, name => { if (name === 'store') storeExpanded.value = true }, { immediate: true })
// Deep links from overview cards should reveal their parent submenu so the active destination is visible.
watch(() => route.meta.parent, parent => {
  if (parent === 'overview') {
    overviewExpanded.value = true
    if (!mobile.value) collapsed.value = false
  }
}, { immediate: true })

const title = computed(() => (route.meta.title ? t(route.meta.title) : ''))

watchEffect(() => { document.title = title.value ? `${title.value} - Bubble Fighter Fantasy` : 'Bubble Fighter Fantasy' })
</script>

<template>
  <div v-if="!server.authReady || (server.startupError && server.startupError !== 'bootstrap_token_required')" class="startup-screen">
    <p class="setup-wordmark">Bubble Fighter Fantasy</p>
    <h1>{{ t(server.startupError ? 'setup.connection_failed' : 'setup.connecting') }}</h1>
    <NoahButton v-if="server.startupError" @click="server.initialize()">{{ t('setup.retry') }}</NoahButton>
  </div>
  <SignInView v-else-if="server.signInRequired" />
  <SetupView v-else-if="server.installation && (server.setupOpen || server.setupStatus?.phase !== 'ready')" />
  <div v-else class="shell" :class="{ 'sidebar-collapsed': collapsed && !mobile, 'builds-shell': route.name === 'builds', 'plugins-shell': route.name === 'plugins', 'overview-shell': route.name === 'overview', 'store-catalog-shell': route.name === 'store' && storePage.id === 'browse' }">
    <div v-if="mobile && ui.sideOpen" class="sider-backdrop" aria-hidden="true" @click="closeSidebar" />
    <aside id="main-sidebar" ref="sidebar" class="sider" :class="{ 'is-open': ui.sideOpen }" :inert="mobile && !ui.sideOpen"
      :role="mobile && ui.sideOpen ? 'dialog' : undefined" :aria-modal="mobile && ui.sideOpen ? true : undefined" :aria-label="t('nav.navigation')" @keydown="sidebarKeydown">
      <div class="sider-brand">
        <img class="brand-mark" :src="odteamMark" alt="" width="22" height="22">
        <span class="brand-text">Bubble Fighter <b>Fantasy</b></span>
        <NoahButton class="sider-toggle" variant="ghost" icon="menu" icon-only
          :title="t(mobile ? 'common.close' : collapsed ? 'nav.expand' : 'nav.collapse')" :aria-label="t(mobile ? 'common.close' : collapsed ? 'nav.expand' : 'nav.collapse')"
          @click="mobile ? closeSidebar() : collapsed = !collapsed" />
      </div>
      <div class="sider-search" :inert="collapsed && !mobile">
        <NoahInput v-model="navSearch" prefix-icon="search" :placeholder="t('nav.search')" :aria-label="t('nav.search')" @keydown.esc="navSearch = ''" />
      </div>
      <nav class="nav" :aria-label="t('nav.navigation')">
        <section v-for="group in groups" :key="group.id" class="nav-group" :aria-label="t(group.title)">
          <div class="nav-group-title">{{ t(group.title) }}</div>
          <div v-for="page in group.pages" :key="page.path" class="nav-entry">
            <div v-if="page.path === 'overview'" class="nav-overview-heading">
              <RouterLink to="/overview" :class="{ 'is-active': route.name === 'overview' }" :title="t(page.title)" :aria-label="t(page.title)" @click="openOverviewMenu"><NoahIcon :name="page.icon" :size="18" /><span class="nav-label">{{ t(page.title) }}</span></RouterLink>
              <button type="button" class="nav-overview-toggle" :aria-label="t(overviewMenuOpen ? 'nav.collapse' : 'nav.expand') + ' ' + t(page.title)" :aria-expanded="overviewMenuOpen && (!collapsed || mobile)" aria-controls="overview-navigation" @click="toggleOverviewMenu"><NoahIcon name="chevron-right" :size="14" :class="{ 'is-expanded': overviewMenuOpen }" /></button>
            </div>
            <button v-else-if="page.path === 'store'" type="button" class="nav-parent" :class="{ 'is-active': route.name === 'store' }"
              :title="t(page.title)" :aria-label="t(page.title)" :aria-expanded="storeMenuOpen && (!collapsed || mobile)" aria-controls="store-navigation" @click="toggleStoreMenu">
              <NoahIcon :name="page.icon" :size="18" />
              <span class="nav-label">{{ t(page.title) }}</span>
              <NoahIcon class="nav-branch" :class="{ 'is-expanded': storeMenuOpen }" name="chevron-right" :size="14" />
            </button>
            <RouterLink v-else :to="'/' + page.path" active-class="is-active" :title="t(page.title)" :aria-label="t(page.title)">
              <NoahIcon :name="page.icon" :size="18" />
              <span class="nav-label">{{ t(page.title) }}</span>
              <span v-if="page.path === 'players' && server.players" class="nav-badge mono">{{ server.players }}</span>
            </RouterLink>
            <div v-if="page.path === 'overview'" id="overview-navigation" class="nav-submenu" :class="{ 'is-expanded': overviewMenuOpen }" :inert="!overviewMenuOpen || collapsed && !mobile" :aria-hidden="!overviewMenuOpen || collapsed && !mobile">
              <div class="nav-submenu-inner"><div class="nav-children">
                <RouterLink v-for="child in filteredOverviewPages" :key="child.path" :to="'/' + child.path" :class="{ 'is-current': route.name === child.path }" :aria-current="route.name === child.path ? 'page' : undefined"><span>{{ t(child.menuTitle || child.title) }}</span></RouterLink>
              </div></div>
            </div>
            <div v-if="page.path === 'store'" id="store-navigation" class="nav-submenu" :class="{ 'is-expanded': storeMenuOpen }" :inert="!storeMenuOpen || collapsed && !mobile" :aria-hidden="!storeMenuOpen || collapsed && !mobile">
              <div class="nav-submenu-inner">
                <div class="nav-children">
                  <RouterLink v-for="child in filteredStorePages" :key="child.id" :to="{ path: '/store', query: child.id === 'browse' ? {} : { view: child.id } }"
                    :class="{ 'is-current': route.name === 'store' && storePage.id === child.id }"
                    :aria-current="route.name === 'store' && storePage.id === child.id ? 'page' : undefined">
                    <span>{{ t(child.title) }}</span>
                  </RouterLink>
                </div>
              </div>
            </div>
          </div>
        </section>
        <p v-if="!groups.length" class="nav-empty">{{ t('nav.noResults') }}</p>
      </nav>
      <div class="sider-footer">
        <RouterLink to="/settings" class="sider-settings" active-class="is-active" :title="t('nav.settings')" :aria-label="t('nav.settings')">
          <NoahIcon name="gear" :size="18" /><span class="nav-label">{{ t('nav.settings') }}</span>
        </RouterLink>
      </div>
    </aside>

    <main class="main" :inert="mobile && ui.sideOpen">
      <div class="breadcrumb-bar">
        <NoahButton v-if="mobile" ref="menuButton" class="page-menu" variant="ghost" icon="menu" icon-only :aria-label="t('menu')" aria-controls="main-sidebar" :aria-expanded="ui.sideOpen" @click="ui.sideOpen = !ui.sideOpen" />
        <nav class="breadcrumbs" :aria-label="t('nav.breadcrumb')">
          <ol>
            <li><RouterLink :to="groupTarget" aria-current-value="location">{{ t(breadcrumbGroup) }}</RouterLink></li>
            <li v-if="route.meta.parent === 'overview'"><span class="breadcrumb-separator" aria-hidden="true">/</span><RouterLink to="/overview">{{ t('nav.overview') }}</RouterLink></li>
            <li><span class="breadcrumb-separator" aria-hidden="true">/</span>
              <RouterLink v-if="route.name === 'store'" to="/store" aria-current-value="location">{{ title }}</RouterLink>
              <span v-else aria-current="page">{{ title }}</span>
            </li>
            <li v-if="route.name === 'store'"><span class="breadcrumb-separator" aria-hidden="true">/</span><span aria-current="page">{{ t(storePage.title) }}</span></li>
          </ol>
        </nav>
      </div>
      <div class="view" :class="{ 'view-settings': route.name === 'settings' }" aria-live="polite">
        <RouterView v-slot="{ Component }">
          <Transition name="page" mode="out-in">
            <div v-if="Component" :key="route.name" class="route-page">
              <div class="page-heading">
                <h1>{{ title }}</h1>
              </div>
              <component :is="Component" />
            </div>
          </Transition>
        </RouterView>
      </div>
    </main>
    <WebUiUpdateNotice />
    <GithubMirrorSuggestion />
    <NoahToastContainer />
  </div>
</template>

<style scoped>
.route-page { display: flex; flex-direction: column; gap: 20px; min-width: 0; }
.page-heading { display: flex; align-items: center; gap: 12px; margin: 0 0 12px; }
.page-heading h1 { margin: 0; font-size: 26px; line-height: 1.4; font-weight: 550; letter-spacing: 0; overflow-wrap: anywhere; }
.page-enter-active { transition: opacity 180ms ease, transform 180ms ease; }
.page-leave-active { transition: opacity 100ms ease; }
.page-enter-from { opacity: 0; transform: translateY(6px); }
.page-leave-to { opacity: 0; }
@media (prefers-reduced-motion: reduce) {
  .page-enter-active, .page-leave-active { transition: none; }
  .page-enter-from { transform: none; }
}
</style>
