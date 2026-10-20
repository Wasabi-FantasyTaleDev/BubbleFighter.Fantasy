<script setup>
import odteamMark from './assets/icon_odteam.png'
import { computed, watchEffect, onMounted } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import NoahIcon from './noah/components/icons/NoahIcon.vue'
import NoahButton from './noah/components/ui/NoahButton.vue'
import NoahSelect from './noah/components/ui/NoahSelect.vue'
import SignInView from './views/SignInView.vue'
import SetupView from './views/SetupView.vue'
import NoahToastContainer from './noah/components/ui/NoahToastContainer.vue'
import { PAGES } from './router'
import { useUiStore } from './stores/ui'
import { useServerStore } from './stores/server'
import { useI18n } from './i18n'
import { LANGS, LANGUAGE_NAMES } from './langs'

const route = useRoute()
const ui = useUiStore()
const server = useServerStore()
const { t } = useI18n()
onMounted(() => server.initialize())

const groups = computed(() => [
  { title: 'nav.group.server', pages: PAGES.filter((p) => p.group === 'server') },
  { title: 'nav.group.system', pages: PAGES.filter((p) => p.group === 'system') }
])
const languageOptions = computed(() => [
  { value: 'auto', label: t('lang.auto') },
  ...LANGS.map((code) => ({ value: code, label: LANGUAGE_NAMES[code], lang: code }))
])
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
  <div v-else class="shell">
    <aside class="sider" :class="{ 'is-open': ui.sideOpen }">
      <div class="sider-brand">
        <img class="brand-mark" :src="odteamMark" alt="" width="22" height="22">
        <span class="brand-text">Bubble Fighter <b>Fantasy</b></span>
      </div>
      <nav class="nav" aria-label="Main" @click="ui.sideOpen = false">
        <template v-for="group in groups" :key="group.title">
          <div class="nav-group-title">{{ t(group.title) }}</div>
          <RouterLink v-for="page in group.pages" :key="page.path" :to="'/' + page.path" active-class="is-active">
            <NoahIcon :name="page.icon" :size="18" />
            <span>{{ t(page.title) }}</span>
            <span v-if="page.path === 'players' && server.players" class="nav-badge mono">{{ server.players }}</span>
          </RouterLink>
        </template>
      </nav>
      <div class="sider-footer">
        <span class="status-dot" :class="{ 'is-ok': server.conn === 'online', 'is-err': server.conn === 'offline' }" />
        <span class="text-caption text-muted">{{ t('conn.' + server.conn) }}</span>
      </div>
    </aside>

    <main class="main">
      <header class="topbar">
        <NoahButton class="menu-btn" variant="ghost" size="sm" icon="menu" icon-only :aria-label="t('menu')" @click="ui.sideOpen = !ui.sideOpen" />
        <h1 class="page-title">{{ title }}</h1>
        <div class="topbar-tools">
          <NoahSelect class="lang-select" size="sm" :model-value="ui.langPref" :options="languageOptions" @change="ui.setLang" />
          <NoahButton class="icon-btn" variant="default" size="sm" icon-only :aria-label="t('theme.toggle')" @click="ui.toggleTheme()">
            <NoahIcon :name="ui.theme === 'dark' ? 'moon' : 'sun'" :size="16" />
          </NoahButton>
          <NoahButton v-if="server.authMode === 'account'" class="icon-btn" variant="default" size="sm" icon="logout" icon-only :aria-label="t('signout')" :title="t('signout')" @click="server.signOut().catch(() => server.markOffline())" />
        </div>
      </header>
      <div class="view" aria-live="polite">
        <RouterView v-slot="{ Component }">
          <component :is="Component" :key="route.name" />
        </RouterView>
      </div>
    </main>
    <NoahToastContainer />
  </div>
</template>
