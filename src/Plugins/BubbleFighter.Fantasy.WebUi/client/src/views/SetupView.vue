<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSwitch from '../noah/components/ui/NoahSwitch.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import { api, jsonRequest } from '../api/client'
import { useServerStore } from '../stores/server'
import { useUiStore } from '../stores/ui'
import { useI18n } from '../i18n'
import { LANGUAGE_NAMES } from '../langs'
import { createBlueField } from '../scenes/blueField'
import SetupNarrator from '../components/SetupNarrator.vue'
import SetupClients from '../components/SetupClients.vue'
import { clientName } from '../components/clientNames'

const server = useServerStore()
const ui = useUiStore()
const { t, te } = useI18n()
const step = ref(server.hasAdministrator ? 6 : 0)
const selectedLanguage = ref(null)
const busy = ref(false)
const importingClients = ref(false)
const error = ref('')
const tested = ref(false)
const editing = ref(false)
const retainPassword = ref(false)
const administrator = reactive({ username: '', password: '', confirm: '' })
const settings = reactive({
  language: ui.lang, serverName: 'Bubble Fighter Fantasy', profile: 'management', clientDataPath: '', clients: [],
  deferClientDataImport: false, databaseKind: 'mysql', sqlitePath: '', mySqlHost: '127.0.0.1', mySqlPort: 3306,
  mySqlDatabase: 'bf_game_db', mySqlUser: 'bff', mySqlPassword: '', mySqlSslMode: 'None',
  playerLogin: 'launcher', publicHost: '', createAccountOnLogin: false
})
const heading = ref(null)
const setupScreen = ref(null)
const sceneCanvas = ref(null)
const tutorialAudio = shallowRef(null)
const introVisible = ref(!server.hasAdministrator)
const introLanguageIndex = ref(0)
const titles = ['language', 'server', 'client', 'database', 'administrator', 'review', 'progress', 'complete']
const stages = ['server', 'client', 'database', 'administrator', 'complete']
const databaseGuideUrl = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/blob/main/docs/installation.md#first-run-setup'
const scrollbarVisible = ref(false)
const activeStage = computed(() => step.value === 7 ? 5 : Math.min(step.value, 4))
const dialogueStage = computed(() => step.value >= 1 && step.value <= 4 ? step.value : step.value === 7 && status.value?.phase === 'ready' && status.value?.installed ? 5 : null)
const introPhrases = [
  { welcome: 'Welcome to', continue: 'Continue', language: 'en' },
  { welcome: '\u6b22\u8fce\u4f7f\u7528', continue: '\u7ee7\u7eed', language: 'zh-Hans' },
  { welcome: '\u6b61\u8fce\u4f7f\u7528', continue: '\u7e7c\u7e8c', language: 'zh-Hant' },
  { welcome: '\ud658\uc601\ud569\ub2c8\ub2e4', continue: '\uacc4\uc18d', language: 'ko' }
]
const introPhrase = computed(() => introPhrases[introLanguageIndex.value])
const status = computed(() => server.setupStatus)
const validAdmin = computed(() => {
  const username = administrator.username.trim().normalize('NFC')
  return username.length >= 3 && username.length <= 64 && /^[\p{L}\p{Nd}._-]+$/u.test(username) &&
    administrator.password.length >= 12 && administrator.password.length <= 256 && administrator.password === administrator.confirm
})
const canContinue = computed(() => {
  if (importingClients.value) return false
  if (step.value === 0) return !!selectedLanguage.value
  if (step.value === 1) return !!settings.serverName.trim()
  if (step.value === 2) return settings.clients.length > 0 && settings.clients.every(c => c.deferClientDataImport || !!c.clientDataPath.trim())
  if (step.value === 3) return tested.value
  if (step.value === 4) return validAdmin.value
  return true
})
function candidateSettings() {
  // The first-run wizard keeps the initial connection path simple; TLS can be configured later by an operator.
  return { ...settings, mySqlPort: Number(settings.mySqlPort), publicHost: settings.publicHost.trim(),
    mySqlSslMode: editing.value ? (settings.mySqlSslMode || 'None') : 'None', clients: settings.clients.map(client => ({
    ...client, clientDataPath: client.clientDataPath.trim(), deferClientDataImport: !client.clientDataPath.trim()
  })) }
}
// The installation binds the launcher on port 8090 unless BFF_LAUNCHER_LISTEN overrides it.
const launcherAddress = computed(() => `${settings.publicHost || window.location.hostname}:8090`)
const adminAddress = window.location.origin
let summaryLoaded = false
const message = (code) => t(te('setup.' + code) ? 'setup.' + code : 'setup.operation_failed')
let timer
let introTimer
let scrollbarTimer
let databaseHeightAnimation
let disposed = false
let blueField

function reducedMotion() { return window.matchMedia('(prefers-reduced-motion: reduce)').matches }

function showScrollbar() {
  scrollbarVisible.value = true
  clearTimeout(scrollbarTimer)
  scrollbarTimer = window.setTimeout(() => { scrollbarVisible.value = false }, 1200)
}
function revealScrollbarAtEdge(event) {
  const element = event.currentTarget
  if (element.scrollHeight > element.clientHeight && element.getBoundingClientRect().right - event.clientX < 20) showScrollbar()
}

function holdDatabaseHeight(element) {
  const container = element.parentElement
  const height = container.getBoundingClientRect().height
  databaseHeightAnimation?.cancel()
  databaseHeightAnimation = null
  container.style.height = `${height}px`
}

function resizeDatabaseFields(element) {
  const container = element.parentElement
  const previousHeight = container.getBoundingClientRect().height
  const nextHeight = element.getBoundingClientRect().height
  if (reducedMotion()) { container.style.height = ''; return }
  container.style.height = `${nextHeight}px`
  const animation = container.animate([{ height: `${previousHeight}px` }, { height: `${nextHeight}px` }], {
    duration: 320, easing: 'cubic-bezier(.2, .8, .2, 1)'
  })
  databaseHeightAnimation = animation
  // Restore intrinsic sizing after the switch so translations and window resizing still fit.
  animation.finished.then(() => {
    if (databaseHeightAnimation !== animation) return
    container.style.height = ''
    databaseHeightAnimation = null
  }).catch(() => {})
}

function beginScene() {
  if (!sceneCanvas.value || !setupScreen.value) return
  blueField = createBlueField(sceneCanvas.value, setupScreen.value)
}

function moveBackdrop(event) {
  if (event.pointerType === 'touch' || reducedMotion() || !setupScreen.value) return
  const bounds = setupScreen.value.getBoundingClientRect()
  const x = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width))
  const y = Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height))
  blueField?.move(x, 1 - y)
}

function resetBackdrop() {
  if (reducedMotion() || !setupScreen.value) return
  blueField?.reset()
}

function rippleBackdrop(event) {
  if (event.pointerType === 'touch' || reducedMotion() || !setupScreen.value) return
  const bounds = setupScreen.value.getBoundingClientRect()
  const x = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width))
  const y = Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height))
  blueField?.ripple(x, 1 - y)
}

function selectLanguage(code) { selectedLanguage.value = code; settings.language = code; ui.setLang(code) }
async function beginAssistant() {
  clearInterval(introTimer)
  introVisible.value = false
  await nextTick()
  heading.value?.focus()
}
async function go(value) {
  error.value = ''; step.value = value
  await nextTick(); heading.value?.focus()
}
async function loadStatus() {
  const value = await server.refreshSetup()
  if (!settings.sqlitePath) settings.sqlitePath = value.defaultSqlitePath
  if (value.hasAdministrator && !editing.value) step.value = value.phase === 'ready' && value.installed ? 7 : 6
  if (step.value === 7 && !summaryLoaded) {
    // After a reload the wizard's own fields are gone; the summary shows what the server actually saved.
    try {
      const saved = await api('setup/configuration')
      settings.playerLogin = saved.settings.playerLogin; settings.publicHost = saved.settings.publicHost || ''
      settings.createAccountOnLogin = saved.settings.createAccountOnLogin === true
      summaryLoaded = true
    } catch { /* The summary keeps the local values when the session cannot read the configuration. */ }
  }
}
async function next() {
  if (busy.value || !canContinue.value) return
  if (step.value === 0) {
    busy.value = true; error.value = ''
    if (!tutorialAudio.value && window.AudioContext) {
      tutorialAudio.value = new AudioContext()
      void tutorialAudio.value.resume().catch(() => { error.value = t('setup.soundUnavailable') })
    }
    try { await loadStatus(); server.startupError = ''; await go(1) }
    catch (e) { error.value = message(e.message === 'bootstrap_token_required' ? e.message : 'connection_failed') }
    finally { busy.value = false }
  } else await go(step.value === 3 && editing.value ? 5 : step.value + 1)
}
async function probe() {
  busy.value = true; error.value = ''; tested.value = false
  try {
    const candidate = candidateSettings()
    await api(editing.value ? 'setup/configuration/probe' : 'setup/probe', jsonRequest('POST', editing.value
      ? { settings: candidate, retainDatabasePassword: retainPassword.value } : candidate))
    tested.value = true
  } catch (e) { error.value = message(e.message) }
  finally { busy.value = false }
}
function invalidate() { tested.value = false; error.value = '' }
watch(settings, invalidate)
watch(step, showScrollbar)
function operationId() {
  // getRandomValues is available on LAN origins too; no administrator secret is stored in browser storage.
  const b = crypto.getRandomValues(new Uint8Array(16)); b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128
  const h = [...b].map(v => v.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}
const claimId = operationId()
async function apply() {
  if (busy.value) return
  busy.value = true; error.value = ''; server.setupOpen = true
  const candidate = candidateSettings()
  try {
    if (editing.value) {
      await api('setup/configuration', jsonRequest('PUT', { settings: candidate, retainDatabasePassword: retainPassword.value }))
    } else {
      try {
        await api('setup/complete', { ...jsonRequest('POST', { operationId: claimId, settings: candidate,
          username: administrator.username, password: administrator.password }), allowUnauthorized: true })
      } catch (e) {
        // A disconnect may occur after the durable claim. Authenticating recovers that operation safely.
        if (e.status !== 0 && e.status !== 401 && e.status !== 409) throw e
      }
      await server.login(administrator.username, administrator.password)
    }
    administrator.password = ''; administrator.confirm = ''; settings.mySqlPassword = ''; server.bootstrapToken = ''
    editing.value = false; await go(6); await loadStatus(); schedulePoll()
  } catch (e) { error.value = message(e.message) }
  finally { busy.value = false }
}
function schedulePoll() {
  clearTimeout(timer)
  if (disposed || status.value?.phase !== 'applying') return
  timer = setTimeout(async () => {
    try { await loadStatus() } catch (e) { error.value = message(e.status === 0 ? 'connection_failed' : e.message) }
    if (!disposed) schedulePoll()
  }, 1200)
}
async function retry() {
  busy.value = true; error.value = ''
  try { await api('setup/retry', jsonRequest('POST', {})); await loadStatus(); schedulePoll() }
  catch (e) { error.value = message(e.message) }
  finally { busy.value = false }
}
async function edit() {
  busy.value = true; error.value = ''
  try {
    const saved = await api('setup/configuration')
    Object.assign(settings, saved.settings)
    settings.mySqlSslMode = saved.settings.mySqlSslMode || 'None'
    settings.clients = saved.settings.clients || (saved.settings.profile === 'management' ? [] : [{
      profile: saved.settings.profile, clientDataPath: saved.settings.clientDataPath,
      deferClientDataImport: saved.settings.deferClientDataImport
    }])
    settings.profile = 'management'; settings.clientDataPath = ''; settings.deferClientDataImport = false
    selectedLanguage.value = settings.language; ui.setLang(settings.language)
    retainPassword.value = saved.hasDatabasePassword; tested.value = false; editing.value = true
    await go(1)
  } catch (e) { error.value = message(e.message) }
  finally { busy.value = false }
}
onMounted(async () => {
  beginScene()
  window.addEventListener('blur', resetBackdrop)
  if (introVisible.value && !reducedMotion()) {
    introTimer = window.setInterval(() => {
      introLanguageIndex.value = (introLanguageIndex.value + 1) % introPhrases.length
    }, 3200)
  }
  if (server.hasAdministrator) {
    try { await loadStatus(); schedulePoll() } catch (e) { error.value = message(e.message) }
  }
})
onBeforeUnmount(() => {
  disposed = true
  window.removeEventListener('blur', resetBackdrop)
  clearTimeout(timer)
  clearTimeout(scrollbarTimer)
  databaseHeightAnimation?.cancel()
  clearInterval(introTimer)
  blueField?.dispose()
  void tutorialAudio.value?.close()
  administrator.password = ''
  administrator.confirm = ''
  settings.mySqlPassword = ''
})
</script>

<template>
  <main ref="setupScreen" class="setup-screen" :class="{ 'is-intro': introVisible }" @pointerenter="moveBackdrop" @pointermove="moveBackdrop" @pointerleave="resetBackdrop" @pointercancel="resetBackdrop" @pointerdown="rippleBackdrop">
    <canvas ref="sceneCanvas" class="setup-scene-canvas" aria-hidden="true"></canvas>
    <Transition name="setup-intro" mode="out-in">
      <section v-if="introVisible" class="setup-intro" aria-labelledby="setup-intro-title">
        <p class="setup-intro-studio">Fantasy Tale Studio</p>
        <div class="setup-intro-copy">
          <Transition name="setup-intro-kicker" mode="out-in">
            <p :key="introPhrase.language" class="setup-intro-kicker" :lang="introPhrase.language">{{ introPhrase.welcome }}</p>
          </Transition>
          <h1 id="setup-intro-title">Bubble Fighter Fantasy</h1>
          <button class="setup-intro-continue" type="button" @click="beginAssistant">
            <Transition name="setup-intro-phrase">
              <span :key="introPhrase.continue" :lang="introPhrase.language">{{ introPhrase.continue }}</span>
            </Transition>
          </button>
        </div>
      </section>
      <section v-else class="setup-assistant-stage" :class="{ 'has-narrator': dialogueStage !== null }">
        <div class="setup-topline"><span>Bubble Fighter Fantasy</span><span>Fantasy Tale Studio</span></div>
        <section class="setup-window" :class="{ 'is-language': step === 0 }" :aria-labelledby="'setup-title'">
      <aside class="setup-aside">
        <p class="setup-eyebrow">{{ t('setup.welcomeLabel') }}</p>
        <h2>{{ t('setup.welcome') }}</h2>
        <p class="setup-aside-copy">{{ t('setup.welcomeDescription') }}</p>
        <ol class="setup-step-list" :aria-label="t('setup.steps')">
          <li v-for="(name, index) in stages" :key="name" :aria-current="activeStage === index + 1 ? 'step' : undefined" :class="{ 'is-current': activeStage === index + 1, 'is-complete': activeStage > index + 1 }">
            <span class="setup-step-number"><NoahIcon v-if="activeStage > index + 1" name="check" :size="13"/><template v-else>{{ index + 1 }}</template></span>
            {{ t('setup.' + name) }}
          </li>
        </ol>
      </aside>
      <div class="setup-content">
        <div class="setup-content-heading">
          <p class="setup-eyebrow">{{ t('setup.assistant') }}</p>
          <h1 id="setup-title" ref="heading" tabindex="-1">{{ t('setup.' + titles[step] + 'Title') }}</h1>
          <p>{{ t('setup.' + titles[step] + 'Description') }}</p>
        </div>
        <form class="setup-form" @submit.prevent="step === 5 ? apply() : next()">
          <div class="setup-page-viewport">
            <Transition name="setup-page" mode="out-in">
              <div :key="step" class="setup-page-body" :class="{ 'is-scrolling': scrollbarVisible }" @scroll.passive="showScrollbar" @pointermove.passive="revealScrollbarAtEdge" @focusin="showScrollbar">
            <div v-if="step === 0" class="setup-language-list" role="group" :aria-label="t('setup.language')">
              <button v-for="code in ['zh-Hans', 'zh-Hant', 'ko', 'en']" :key="code" type="button" :lang="code" :aria-pressed="selectedLanguage === code" :class="{ 'is-selected': selectedLanguage === code }" @click="selectLanguage(code)">
                <span class="language-name" :lang="code">{{ LANGUAGE_NAMES[code] }}</span><NoahIcon v-if="selectedLanguage === code" name="check" :size="18"/>
              </button>
              <div v-if="server.startupError === 'bootstrap_token_required'" class="setup-field">
                <label for="setup-token">{{ t('setup.bootstrapToken') }}</label>
                <NoahInput id="setup-token" v-model="server.bootstrapToken" type="password" autocomplete="off" maxlength="256" />
                <p class="setup-hint">{{ t('setup.bootstrapDescription') }}</p>
              </div>
            </div>
            <template v-else-if="step === 1">
              <div class="setup-field"><label for="server-name">{{ t('setup.serverName') }}</label><NoahInput id="server-name" v-model="settings.serverName" required maxlength="80" /></div>
              <p class="setup-field-label" id="player-login-label">{{ t('setup.playerLogin') }}</p>
              <div class="setup-choice-row" role="group" aria-labelledby="player-login-label">
                <button v-for="mode in ['launcher', 'open']" :key="mode" type="button" :aria-pressed="settings.playerLogin === mode" :class="{ 'is-selected': settings.playerLogin === mode }" @click="settings.playerLogin = mode"><strong>{{ t(mode === 'launcher' ? 'setup.loginLauncher' : 'setup.loginOpen') }}</strong><span>{{ t(mode === 'launcher' ? 'setup.loginLauncherDescription' : 'setup.loginOpenDescription') }}</span></button>
              </div>
              <div v-if="settings.playerLogin === 'launcher'" class="setup-field setup-switch-field"><NoahSwitch id="create-account" v-model="settings.createAccountOnLogin" aria-labelledby="create-account-label" /><div><p class="setup-field-label" id="create-account-label">{{ t('setup.createAccountOnLogin') }}</p><p class="setup-hint">{{ t('setup.createAccountOnLoginHelp') }}</p></div></div>
              <div class="setup-field"><label for="public-host">{{ t('setup.publicHost') }}</label><NoahInput id="public-host" v-model="settings.publicHost"  maxlength="253" autocomplete="off" :placeholder="t('setup.publicHostAuto')" /><p class="setup-hint">{{ t('setup.publicHostHelp') }}</p></div>
            </template>
            <template v-else-if="step === 2">
              <SetupClients v-model="settings.clients" :profiles="status?.profiles || []" @busy="importingClients = $event" />
            </template>
            <template v-else-if="step === 3">
              <div class="setup-choice-row" role="group" :aria-label="t('setup.database')">
                <button v-for="kind in ['mysql', 'sqlite']" :key="kind" type="button" :aria-pressed="settings.databaseKind === kind" :class="{ 'is-selected': settings.databaseKind === kind }" @click="settings.databaseKind = kind; retainPassword = false; invalidate()"><strong>{{ kind === 'sqlite' ? 'SQLite' : 'MySQL' }}</strong><span>{{ t('setup.' + kind + 'Description') }}</span></button>
              </div>
              <div class="setup-database-fields">
              <Transition name="setup-database" mode="out-in" @before-leave="holdDatabaseHeight" @enter="resizeDatabaseFields">
              <div v-if="settings.databaseKind === 'sqlite'" key="sqlite" class="setup-field"><label for="sqlite-path">{{ t('setup.sqlitePath') }}</label><NoahInput id="sqlite-path" v-model="settings.sqlitePath" required maxlength="2048" @input="invalidate" /><p class="setup-hint">{{ t('setup.sqliteProbeDescription') }}</p></div>
              <div v-else key="mysql" class="setup-field-grid" @input="invalidate">
                <div class="setup-field"><label for="mysql-host">{{ t('setup.host') }}</label><NoahInput id="mysql-host" v-model="settings.mySqlHost" required maxlength="253" @change="retainPassword = false" /></div>
                <div class="setup-field"><label for="mysql-port">{{ t('setup.port') }}</label><NoahInput id="mysql-port" v-model="settings.mySqlPort" type="number" min="1" max="65535" required @change="retainPassword = false" /></div>
                <div class="setup-field"><label for="mysql-db">{{ t('setup.databaseName') }}</label><NoahInput id="mysql-db" v-model="settings.mySqlDatabase" required maxlength="64" /></div>
                <div class="setup-field"><label for="mysql-user">{{ t('setup.databaseUser') }}</label><NoahInput id="mysql-user" v-model="settings.mySqlUser" required maxlength="128" @change="retainPassword = false" /></div>
                <div class="setup-field setup-field-wide"><label for="mysql-password">{{ t('setup.databasePassword') }}</label><NoahInput id="mysql-password" v-model="settings.mySqlPassword" type="password" autocomplete="off" maxlength="4096" :show-password-label="t('setup.showPassword')" :hide-password-label="t('setup.hidePassword')" @input="retainPassword = false" /><p v-if="retainPassword" class="setup-hint">{{ t('setup.retainedPassword') }}</p></div>
                <!-- Connection security is intentionally fixed to None in the first-run wizard. -->
              </div>
              </Transition>
              </div>
              <div class="setup-probe"><NoahButton :loading="busy" @click="probe">{{ t('setup.testConnection') }}</NoahButton></div>
            </template>
            <template v-else-if="step === 4">
              <div class="setup-field"><label for="admin-username">{{ t('setup.username') }}</label><NoahInput id="admin-username" v-model="administrator.username" name="username" autocomplete="username" required minlength="3" maxlength="64" /><p class="setup-hint">{{ t('setup.usernameHelp') }}</p></div>
              <div class="setup-field"><label for="admin-password">{{ t('setup.password') }}</label><NoahInput id="admin-password" v-model="administrator.password" name="password" type="password" autocomplete="new-password" required minlength="12" maxlength="256" :show-password-label="t('setup.showPassword')" :hide-password-label="t('setup.hidePassword')" /><p class="setup-hint">{{ t('setup.passwordHelp') }}</p></div>
              <div class="setup-field"><label for="admin-confirm">{{ t('setup.confirmPassword') }}</label><NoahInput id="admin-confirm" v-model="administrator.confirm" type="password" autocomplete="new-password" required minlength="12" maxlength="256" :show-password-label="t('setup.showPassword')" :hide-password-label="t('setup.hidePassword')" /></div>
              <p class="setup-callout">{{ t('setup.separateAdministrator') }}</p>
            </template>
            <template v-else-if="step === 5">
              <dl class="setup-summary">
                <div><dt>{{ t('setup.language') }}</dt><dd class="language-name" :lang="settings.language">{{ LANGUAGE_NAMES[settings.language] }}</dd></div>
                <div><dt>{{ t('setup.serverName') }}</dt><dd>{{ settings.serverName }}</dd></div>
                <div><dt>{{ t('setup.playerLoginSummary') }}</dt><dd>{{ t(settings.playerLogin === 'launcher' ? 'setup.loginLauncher' : 'setup.loginOpen') }}</dd></div>
                <div v-if="settings.playerLogin === 'launcher'"><dt>{{ t('setup.createAccountOnLogin') }}</dt><dd>{{ t(settings.createAccountOnLogin ? 'setup.yes' : 'setup.no') }}</dd></div>
                <div><dt>{{ t('setup.publicHost') }}</dt><dd>{{ settings.publicHost.trim() || t('setup.publicHostAuto') }}</dd></div>
                <div v-for="client in settings.clients" :key="client.profile"><dt>{{ clientName(status?.profiles || [], client.profile, ui.lang) }}</dt><dd>{{ client.deferClientDataImport ? t('setup.skipImport') : client.clientDataPath }}</dd></div>
                <div><dt>{{ t('setup.database') }}</dt><dd>{{ settings.databaseKind === 'sqlite' ? 'SQLite' : 'MySQL' }}</dd></div>
                <div><dt>{{ t(settings.databaseKind === 'sqlite' ? 'setup.sqlitePath' : 'setup.host') }}</dt><dd>{{ settings.databaseKind === 'sqlite' ? settings.sqlitePath : `${settings.mySqlHost}:${settings.mySqlPort}` }}</dd></div>
                <div v-if="!editing"><dt>{{ t('setup.username') }}</dt><dd>{{ administrator.username }}</dd></div>
              </dl>
              <p class="setup-callout">{{ t('setup.reviewNotice') }}</p>
            </template>
            <div v-else-if="step === 7" class="setup-complete">
              <p>{{ t('setup.completeDescription') }}</p>
              <dl class="setup-summary">
                <div><dt>{{ t('setup.summaryPlayers') }}</dt><dd>{{ settings.publicHost || t('setup.publicHostAuto') }}</dd></div>
                <div v-if="settings.playerLogin === 'launcher'"><dt>{{ t('setup.summaryLauncher') }}</dt><dd>{{ launcherAddress }}</dd></div>
                <div><dt>{{ t('setup.summaryAdmin') }}</dt><dd>{{ adminAddress }}</dd></div>
              </dl>
              <h2 class="setup-summary-heading">{{ t('setup.summaryNext') }}</h2>
              <p>{{ t(settings.playerLogin === 'launcher' ? 'setup.nextLauncher' : 'setup.nextOpen') }}</p>
              <p v-if="status?.clientDataDeferred" class="setup-callout">{{ t('setup.deferredDescription') }} {{ t('setup.nextDeferred') }}</p>
              <NoahButton variant="primary" @click="server.setupOpen = false">{{ t('setup.openDashboard') }}</NoahButton>
            </div>
            <div v-else class="setup-progress" aria-live="polite">
              <span v-if="status?.phase === 'applying'" class="setup-spinner" aria-hidden="true"/>
              <NoahIcon v-else name="alert" :size="36"/>
              <h2>{{ t(status?.phase === 'failed' ? 'setup.failed' : 'setup.applying') }}</h2>
              <p>{{ status?.phase === 'failed' ? message(status.errorCode) : t('setup.applyingDescription') }}</p>
              <div v-if="status?.phase === 'failed'" class="setup-progress-actions"><NoahButton :loading="busy" @click="retry">{{ t('setup.retry') }}</NoahButton><NoahButton variant="primary" :disabled="busy" @click="edit">{{ t('setup.edit') }}</NoahButton></div>
            </div>
              </div>
            </Transition>
          </div>
            <p v-if="step === 3" id="setup-connection-status" class="setup-connection-status" :class="{ 'is-verified': tested && !error, 'is-error': !!error }" :role="error ? 'alert' : 'status'" :aria-live="error ? 'assertive' : 'polite'" aria-atomic="true">{{ error || t(tested ? 'setup.connectionSuccess' : 'setup.testConnectionRequired') }}</p>
            <p v-else-if="error" class="setup-error" role="alert">{{ error }}</p>
            <footer v-if="step < 6" class="setup-actions">
              <span v-if="step === 0" class="setup-hint">{{ t('setup.changeLater') }}</span>
              <NoahButton v-else variant="ghost" :disabled="busy" @click="go(step === 5 && editing ? 3 : step - 1)">{{ t('setup.back') }}</NoahButton>
              <div class="setup-actions-next">
                <a v-if="step === 3" class="setup-database-guide-button" :href="databaseGuideUrl" target="_blank" rel="noopener noreferrer">{{ t('setup.databaseGuide') }}</a>
                <NoahButton variant="primary" size="lg" html-type="submit" :aria-describedby="step === 3 ? 'setup-connection-status' : undefined" :disabled="!canContinue || busy" :loading="busy">{{ t(step === 5 ? 'setup.install' : 'setup.continue') }}</NoahButton>
              </div>
            </footer>
          </form>
      </div>
        </section>
        <Transition name="setup-narrator-dock">
          <SetupNarrator v-if="dialogueStage !== null" :stage="dialogueStage ?? 1" :language="ui.lang" :audio-context="tutorialAudio" />
        </Transition>
        <!-- <p class="setup-bottomline">{{ t('setup.footer') }}</p> -->
      </section>
    </Transition>
  </main>
</template>
