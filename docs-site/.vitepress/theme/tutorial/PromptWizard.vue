<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { withBase } from 'vitepress'
import { useI18n } from 'vue-i18n'
import TpIcon from './kit/TpIcon.vue'
import { buildPrompt, engines, type PromptChoice } from './data/install-prompt'
import type { Code } from './types'

// The window behind "Copy prompt": a few questions about the reader's setup, then the prompt
// written for it. A reader who only wants to play on their own PC is pointed to the launcher first.

const props = defineProps<{ lang: Code; agent: string }>()
const emit = defineEmits<{ close: [] }>()
const { t: label } = useI18n()
const w = (key: string, values?: Record<string, unknown>) => label(`tutorial.wizard.${key}`, { agent: props.agent, ...values })

type Page = 'where' | 'launcher' | 'os' | 'runtime' | 'db' | 'agent' | 'result'
const STORE = 'bff-tutorial-prompt'
const blank: PromptChoice = { target: 'server', os: 'windows', db: 'install', engine: 'mysql', dbUser: 'bff', dbPassword: '', agent: 'pc', host: '', sshUser: '', runtime: 'native', docker: 'installed' }
const choice = reactive<PromptChoice>({ ...blank })
// Answers come back on the next visit, except the database question, which always opens on its default:
// SQLite on the reader's own PC (no database server to install or keep running), "Yes, install" on a server.
// The database account never leaves this page: neither the username nor the password is stored or sent anywhere.
try {
  const saved = JSON.parse(localStorage.getItem(STORE) ?? 'null')
  if (saved && typeof saved === 'object') Object.assign(choice, saved, { db: blank.db, dbUser: blank.dbUser, dbPassword: '' })
  if (choice.target === 'pc') choice.db = 'sqlite'
} catch { /* storage unavailable: start from the defaults */ }
watch(choice, value => {
  try { localStorage.setItem(STORE, JSON.stringify({ ...value, dbUser: '', dbPassword: '' })) } catch { /* ignore */ }
})
watch(() => choice.target, target => { choice.db = target === 'pc' ? 'sqlite' : 'install' })
// The usual account differs: a new database gets its own user, an existing one usually has root.
watch(() => choice.db, (db, old) => {
  if (db === 'install' && (!choice.dbUser || choice.dbUser === 'root')) choice.dbUser = 'bff'
  if (db === 'existing' && (!choice.dbUser || (old === 'install' && choice.dbUser === 'bff'))) choice.dbUser = 'root'
})

const history = ref<Page[]>(['where'])
const page = computed(() => history.value[history.value.length - 1])
// Only a Linux server asks how to run: natively as a service or with Docker Compose.
const linuxServer = computed(() => choice.target === 'server' && choice.os === 'linux')
const docker = computed(() => linuxServer.value && choice.runtime === 'docker')
const flow = computed<Page[]>(() => ['where', 'os', ...(linuxServer.value ? ['runtime' as Page] : []), 'db', ...(choice.target === 'server' ? ['agent' as Page] : []), 'result'])
const position = computed(() => Math.max(0, flow.value.indexOf(page.value === 'launcher' ? 'where' : page.value)))
const direction = ref<'forward' | 'back'>('forward')
const dialog = ref<HTMLElement>()

// The window glides to the height of each page instead of jumping. Height cannot transition from
// auto, so the body gets the measured height of its content; the gap between an old page leaving
// and a new one entering is skipped so the window does not collapse in between.
const sizer = ref<HTMLElement>()
const bodyHeight = ref<number>()
const sizing = ref(false)
let watcher: ResizeObserver | undefined
let settle: ReturnType<typeof setTimeout> | undefined
onMounted(() => {
  watcher = new ResizeObserver(() => {
    const el = sizer.value
    if (!el || !el.querySelector('.pw-page')) return
    const height = el.offsetHeight
    if (height === bodyHeight.value) return
    if (bodyHeight.value !== undefined) {
      sizing.value = true
      // transitionend does not fire when max-height clamps the change, so clear the flag anyway.
      clearTimeout(settle); settle = setTimeout(() => (sizing.value = false), 500)
    }
    bodyHeight.value = height
  })
  if (sizer.value) watcher.observe(sizer.value)
})
onUnmounted(() => watcher?.disconnect())

function open(next: Page) { direction.value = 'forward'; history.value.push(next); focusSoon() }
function back() { if (history.value.length > 1) { direction.value = 'back'; history.value.pop(); focusSoon() } }
function next() {
  if (page.value === 'where') open(choice.target === 'pc' ? 'launcher' : 'os')
  else if (page.value === 'launcher') open('os')
  else if (page.value === 'os') open(linuxServer.value ? 'runtime' : 'db')
  else if (page.value === 'runtime') open('db')
  else if (page.value === 'db') open(choice.target === 'server' ? 'agent' : 'result')
  else if (page.value === 'agent') open('result')
}
function restart() { direction.value = 'back'; history.value = ['where']; copied.value = 'idle'; focusSoon() }
function focusSoon() { nextTick(() => dialog.value?.querySelector<HTMLElement>('[data-autofocus]')?.focus({ focusVisible: false } as FocusOptions)) }
focusSoon()

const text = computed(() => buildPrompt(choice, props.lang))
// The preview hides the account the reader typed; the copied prompt keeps the real values. If
// copying fails, the real text is shown so it can be selected by hand.
const MASK = '********'
const reveal = ref(false)
const shown = computed(() => reveal.value ? text.value : buildPrompt({ ...choice, dbUser: choice.dbUser.trim() ? MASK : '', dbPassword: choice.dbPassword ? MASK : '' }, props.lang))
// Run once on the server in PowerShell as administrator: installs, starts and enables OpenSSH Server.
const opensshCommand = ['Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0', 'Start-Service sshd', 'Set-Service sshd -StartupType Automatic'].join('\n')
const launcherGuide = computed(() => withBase(`/${({ en: 'en/', 'zh-CN': '', 'zh-TW': 'zh-tw/', ko: 'ko/' } as Record<Code, string>)[props.lang]}guides/launcher`))
const summary = computed(() => [
  w(choice.target === 'server' ? 'server' : 'pc'),
  choice.os === 'windows' ? 'Windows' : 'Linux',
  ...(linuxServer.value ? [docker.value ? 'Docker' : w('native')] : []),
  choice.db === 'sqlite' ? 'SQLite' : choice.db === 'install' ? (docker.value ? engines.mariadb : engines[choice.engine]) : w('dbExistingShort'),
  ...(choice.target === 'server' ? [w(choice.agent === 'server' ? 'agentOnServerShort' : 'agentOnPcShort')] : []),
])

const copied = ref<'idle' | 'done' | 'failed'>('idle')
const output = ref<HTMLElement>()
async function copy() {
  try { await navigator.clipboard.writeText(text.value); copied.value = 'done' }
  catch {
    // The Clipboard API needs a focused, secure page. Try the older copy command on a hidden
    // field holding the real text; if that fails too, unmask the preview and select it.
    const field = document.createElement('textarea')
    field.value = text.value
    field.setAttribute('readonly', '')
    field.style.cssText = 'position:fixed;top:0;left:0;opacity:0;pointer-events:none'
    dialog.value?.appendChild(field)
    field.select()
    let done = false
    try { done = document.execCommand('copy') } catch { /* unsupported */ }
    field.remove()
    copied.value = done ? 'done' : 'failed'
    if (!done) {
      reveal.value = true
      await nextTick()
      const range = document.createRange()
      if (output.value) { range.selectNodeContents(output.value); getSelection()?.removeAllRanges(); getSelection()?.addRange(range) }
    }
  }
}
watch(text, () => { copied.value = 'idle'; reveal.value = false })
</script>

<template>
  <div class="pw-mask" @click.self="emit('close')" @keydown.esc="emit('close')">
    <section ref="dialog" class="pw" role="dialog" aria-modal="true" aria-labelledby="pw-title">
      <header class="pw-head">
        <div>
          <h2 id="pw-title">{{ w('title') }}</h2>
          <p>{{ w('lead') }}</p>
        </div>
        <button class="pw-close" :aria-label="w('close')" :title="w('close')" @click="emit('close')"><TpIcon name="close" :size="18" /></button>
      </header>
      <ol class="pw-progress" aria-hidden="true">
        <li v-for="(item, n) in flow" :key="item" :class="{ 'is-done': n < position, 'is-current': n === position }" />
      </ol>

      <div class="pw-body" :class="{ 'is-sizing': sizing }" :style="{ height: bodyHeight === undefined ? undefined : `${bodyHeight}px` }" @transitionend.self="sizing = false">
        <div ref="sizer" class="pw-sizer">
        <Transition :name="`pw-${direction}`" mode="out-in">
          <div :key="page" class="pw-page">
            <!-- 1. Where the server goes -->
            <template v-if="page === 'where'">
              <h3>{{ w('whereQ') }}</h3>
              <div class="pw-options" role="radiogroup">
                <button v-for="item in (['server', 'pc'] as const)" :key="item" class="pw-option" role="radio" :aria-checked="choice.target === item" :data-autofocus="choice.target === item ? '' : undefined" @click="choice.target = item">
                  <TpIcon :name="item === 'server' ? 'server' : 'pc'" :size="24" />
                  <b>{{ w(item) }}</b><small>{{ w(`${item}Desc`) }}</small>
                </button>
              </div>
            </template>

            <!-- 1b. Playing on one's own PC needs no server -->
            <template v-else-if="page === 'launcher'">
              <div class="pw-launcher">
                <span class="pw-launcher-icon"><TpIcon name="gamepad" :size="28" /></span>
                <h3>{{ w('launcherTitle') }}</h3>
                <p>{{ w('launcherBody') }}</p>
                <a class="pw-primary" :href="launcherGuide" data-autofocus>{{ w('launcherOpen') }}<TpIcon name="arrowUpRight" :size="16" /></a>
              </div>
            </template>

            <!-- 2. Operating system -->
            <template v-else-if="page === 'os'">
              <h3>{{ w(choice.target === 'server' ? 'osServerQ' : 'osPcQ') }}</h3>
              <div class="pw-options" role="radiogroup">
                <button v-for="item in (['windows', 'linux'] as const)" :key="item" class="pw-option" role="radio" :aria-checked="choice.os === item" :data-autofocus="choice.os === item ? '' : undefined" @click="choice.os = item">
                  <TpIcon :name="item === 'windows' ? 'windows' : 'terminal'" :size="24" />
                  <b>{{ item === 'windows' ? 'Windows' : 'Linux' }}</b>
                </button>
              </div>
            </template>

            <!-- 2b. Linux servers: native service or Docker -->
            <template v-else-if="page === 'runtime'">
              <h3>{{ w('runtimeQ') }}</h3>
              <div class="pw-options" role="radiogroup">
                <button v-for="item in (['native', 'docker'] as const)" :key="item" class="pw-option" role="radio" :aria-checked="choice.runtime === item" :data-autofocus="choice.runtime === item ? '' : undefined" @click="choice.runtime = item">
                  <TpIcon :name="item === 'native' ? 'terminal' : 'box'" :size="24" />
                  <b>{{ item === 'native' ? w('native') : 'Docker' }}</b>
                  <small>{{ w(item === 'native' ? 'nativeDesc' : 'dockerDesc') }}</small>
                </button>
              </div>
              <Transition name="pw-grow">
                <div v-if="choice.runtime === 'docker'" class="pw-sub">
                  <p class="pw-fields-title">{{ w('dockerQ') }}</p>
                  <div class="pw-pills" role="radiogroup">
                    <button v-for="item in (['installed', 'install'] as const)" :key="item" class="pw-pill" role="radio" :aria-checked="choice.docker === item" @click="choice.docker = item">{{ w(item === 'installed' ? 'dockerInstalled' : 'dockerInstall') }}</button>
                  </div>
                </div>
              </Transition>
            </template>

            <!-- 3. Database -->
            <template v-else-if="page === 'db'">
              <h3>{{ w('dbQ') }}</h3>
              <p v-if="choice.target === 'pc'" class="pw-hint is-plain"><TpIcon name="check" :size="15" />{{ w('pcSqliteHint') }}</p>
              <div class="pw-options" role="radiogroup">
                <div class="pw-option" role="radio" tabindex="0" :aria-checked="choice.db === 'install'" :data-autofocus="choice.db === 'install' ? '' : undefined" @click="choice.db = 'install'" @keydown.enter.space.self.prevent="choice.db = 'install'">
                  <TpIcon name="database" :size="24" />
                  <b>{{ w('dbYes') }}</b>
                  <span v-if="docker" class="pw-fixed">{{ w('dockerDbEngine') }}</span>
                  <span v-else class="pw-select">
                    <select v-model="choice.engine" :aria-label="w('dbEngine')" @click.stop="choice.db = 'install'" @focus="choice.db = 'install'">
                      <option value="mysql">{{ engines.mysql }}</option>
                      <option value="mariadb">{{ engines.mariadb }}</option>
                    </select>
                    <TpIcon name="chevronDown" :size="16" />
                  </span>
                </div>
                <div class="pw-option" role="radio" tabindex="0" :aria-checked="choice.db !== 'install'" :data-autofocus="choice.db !== 'install' ? '' : undefined" @click="choice.db === 'install' && (choice.db = 'sqlite')" @keydown.enter.space.self.prevent="choice.db === 'install' && (choice.db = 'sqlite')">
                  <TpIcon name="check" :size="24" />
                  <b>{{ w('dbNo') }}</b>
                  <span class="pw-select">
                    <select :value="choice.db === 'install' ? 'sqlite' : choice.db" :aria-label="w('dbNo')" @click.stop @change="choice.db = ($event.target as HTMLSelectElement).value as PromptChoice['db']" @focus="choice.db === 'install' && (choice.db = 'sqlite')">
                      <option value="sqlite">{{ w('dbSqlite') }}</option>
                      <option value="existing">{{ w('dbExisting') }}</option>
                    </select>
                    <TpIcon name="chevronDown" :size="16" />
                  </span>
                </div>
              </div>
              <Transition name="pw-grow">
                <p v-if="docker && choice.db === 'install'" key="docker" class="pw-hint is-plain"><TpIcon name="lock" :size="15" />{{ w('dockerDbNote') }}</p>
                <div v-else-if="choice.db !== 'sqlite'" key="account" class="pw-fields">
                  <p class="pw-fields-title">{{ w(choice.db === 'install' ? 'accountNew' : 'accountExisting') }}</p>
                  <label><span>{{ w('user') }}</span><input v-model="choice.dbUser" autocomplete="off" spellcheck="false" :placeholder="choice.db === 'install' ? 'bff' : 'root'"></label>
                  <label><span>{{ w('password') }}</span><input v-model="choice.dbPassword" type="password" autocomplete="new-password" :placeholder="w(choice.db === 'install' ? 'passwordGenerate' : 'passwordAsk')"></label>
                  <p class="pw-hint"><TpIcon name="lock" :size="15" />{{ w('privacyNote') }}</p>
                  <p class="pw-hint"><TpIcon name="alert" :size="15" />{{ w('passwordNote') }}</p>
                </div>
                <p v-else key="sqlite" class="pw-hint is-plain">{{ w('sqliteHint') }}</p>
              </Transition>
            </template>

            <!-- 4. Where the agent runs -->
            <template v-else-if="page === 'agent'">
              <h3>{{ w('agentQ') }}</h3>
              <div class="pw-options" role="radiogroup">
                <button v-for="item in (['server', 'pc'] as const)" :key="item" class="pw-option" role="radio" :aria-checked="choice.agent === item" :data-autofocus="choice.agent === item ? '' : undefined" @click="choice.agent = item">
                  <TpIcon :name="item === 'server' ? 'server' : 'pc'" :size="24" />
                  <b>{{ w(item === 'server' ? 'agentOnServer' : 'agentOnPc') }}</b>
                  <small>{{ w(item === 'server' ? 'agentOnServerDesc' : 'agentOnPcDesc') }}</small>
                </button>
              </div>
              <Transition name="pw-grow">
                <div v-if="choice.agent === 'pc'" class="pw-fields">
                  <p class="pw-fields-title">{{ w('sshTitle') }}</p>
                  <label><span>{{ w('host') }}</span><input v-model="choice.host" autocomplete="off" spellcheck="false" placeholder="203.0.113.10"></label>
                  <label><span>{{ w('sshUser') }}</span><input v-model="choice.sshUser" autocomplete="off" spellcheck="false" :placeholder="choice.os === 'windows' ? 'Administrator' : 'root'"></label>
                  <p class="pw-hint"><TpIcon name="key" :size="15" />{{ w('sshNote') }}</p>
                </div>
              </Transition>
              <!-- A Windows server has no SSH server by default; without it the agent cannot reach it. -->
              <Transition name="pw-grow">
                <div v-if="choice.agent === 'pc' && choice.os === 'windows'" class="pw-callout">
                  <p><TpIcon name="alert" :size="16" /><b>{{ w('opensshTitle') }}</b></p>
                  <p>{{ w('opensshBody') }}</p>
                  <pre><code>{{ opensshCommand }}</code></pre>
                  <p class="pw-callout-links">
                    <a href="https://github.com/PowerShell/Win32-OpenSSH/releases/latest" target="_blank" rel="noopener">{{ w('opensshInstaller') }}<TpIcon name="arrowUpRight" :size="14" /></a>                  </p>
                </div>
              </Transition>
            </template>

            <!-- 5. The prompt -->
            <template v-else>
              <h3>{{ w('resultTitle') }}</h3>
              <p class="pw-summary"><span v-for="item in summary" :key="item">{{ item }}</span></p>
              <pre ref="output" class="pw-output"><code>{{ shown }}</code></pre>
              <p class="pw-status" role="status">{{ copied === 'done' ? w('copiedNote') : copied === 'failed' ? label('tutorial.copyFailed') : '' }}</p>
            </template>
          </div>
        </Transition>
        </div>
      </div>

      <footer class="pw-foot">
        <button v-if="history.length > 1" class="pw-secondary" @click="back"><TpIcon name="prev" :size="16" />{{ w('back') }}</button>
        <button v-if="page === 'result'" class="pw-link" @click="restart">{{ w('restart') }}</button>
        <span class="pw-spacer" />
        <button v-if="page === 'launcher'" class="pw-secondary" @click="next">{{ w('launcherContinue') }}</button>
        <button v-else-if="page === 'result'" class="pw-primary" data-autofocus @click="copy"><TpIcon :name="copied === 'done' ? 'check' : 'copy'" :size="16" />{{ copied === 'done' ? label('tutorial.copied') : label('tutorial.copy') }}</button>
        <button v-else class="pw-primary" @click="next">{{ w('next') }}<TpIcon name="next" :size="16" /></button>
      </footer>
    </section>
  </div>
</template>
