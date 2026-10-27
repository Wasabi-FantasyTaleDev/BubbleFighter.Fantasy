<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTerminal from '../noah/components/ui/NoahTerminal.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSwitch from '../noah/components/ui/NoahSwitch.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useI18n } from '../i18n'

// The view keeps at most this many lines, so a long-open page cannot grow without bound.
const props = defineProps({ interactive: Boolean })
const MAX_LINES = 1000
const POLL_MS = 2000
const RANK = { debug: 0, info: 1, warn: 2, error: 3 }

const { t } = useI18n()
const lines = ref([])
const error = ref(null)
const loaded = ref(false)
const paused = ref(false)
const minLevel = ref('debug')
const terminal = ref(null)
const command = ref('')
const maintenance = ref(false)
const maintenanceAvailable = ref(false)
const savingMaintenance = ref(false)
const commands = ref([])
const actionError = ref('')
const restartState = ref('')
const previewInfoOpen = ref(false)
const restartConfirmOpen = ref(false)
const maximized = ref(false)
const consoleCard = ref(null)
let previousFocus = null
let savedBodyOverflow = ''

async function setMaximized(value) {
  if (value === maximized.value) return
  const card = consoleCard.value?.$el
  const before = card?.getBoundingClientRect()
  if (value) {
    previousFocus = document.activeElement
    savedBodyOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
  } else document.body.style.overflow = savedBodyOverflow
  maximized.value = value
  await nextTick()
  const element = consoleCard.value?.$el
  if (element && before && !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    const after = element.getBoundingClientRect()
    element.animate([
      { transformOrigin: 'top left', transform: `translate(${before.left - after.left}px, ${before.top - after.top}px) scale(${before.width / after.width}, ${before.height / after.height})`, opacity: .85 },
      { transformOrigin: 'top left', transform: 'none', opacity: 1 }
    ], { duration: 220, easing: 'cubic-bezier(.2,.8,.2,1)' })
  }
  if (value) element?.querySelector('.dot-maximize')?.focus({ preventScroll: true })
  else if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true })
  await followOutput()
}
function onEscape(event) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  if (restartConfirmOpen.value) { restartConfirmOpen.value = false; return }
  if (previewInfoOpen.value) { previewInfoOpen.value = false; return }
  if (maximized.value && !document.querySelector('.noah-select-dropdown, .noah-modal-overlay')) {
    event.preventDefault()
    setMaximized(false)
  }
}
const commandLines = ref([])
const quickCommands = computed(() => [
  { label: t('console.help'), icon: 'book', command: 'help' },
  { label: t('console.status'), icon: 'activity', command: 'status' },
  { label: t('nav.players'), icon: 'players', command: 'players' },
  { label: t('nav.rooms'), icon: 'rooms', command: 'rooms' }
].filter(item => commands.value.some(definition => definition.name === item.command)))
async function submitCommand(value = command.value) {
  const text = value.trim()
  if (!text) return
  if (/^restart(?:\s|$)/i.test(text)) { restartConfirmOpen.value = true; return }
  await executeCommand(text)
}
async function executeCommand(text) {
  actionError.value = ''
  try {
    const result = await api('commands/execute', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text }) })
    commandLines.value.push({ id: ++commandSequence, command: text, message: result.message })
  } catch (e) {
    actionError.value = e.message
    commandLines.value.push({ id: ++commandSequence, command: text, message: e.message })
  }
  if (commandLines.value.length > 100) commandLines.value.splice(0, commandLines.value.length - 100)
  command.value = ''
  await followOutput()
}
async function toggleMaintenance(value) {
  if (!maintenanceAvailable.value || savingMaintenance.value) return
  savingMaintenance.value = true
  actionError.value = ''
  try {
    const result = await api('maintenance', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled: value }) })
    maintenance.value = result.enabled
  } catch (e) {
    actionError.value = e.message
  } finally { savingMaintenance.value = false }
}
let commandSequence = 0

async function restartServer() {
  restartConfirmOpen.value = false
  if (restartState.value === 'waiting') return
  actionError.value = ''
  restartState.value = 'waiting'
  try {
    const before = await api('health')
    await api('restart', { method: 'POST' })
    const deadline = Date.now() + 120000
    while (Date.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 750))
      try {
        const current = await api('health')
        if (current.startedUtc !== before.startedUtc) {
          restartState.value = 'success'
          cursor = 0
          lines.value = []
          await poll()
          schedule()
          setTimeout(() => { if (restartState.value === 'success') restartState.value = '' }, 5000)
          return
        }
      } catch { /* The listener is expected to be unavailable while the old process exits. */ }
    }
    throw new Error(t('console.restartTimeout'))
  } catch (e) {
    restartState.value = 'failed'
    actionError.value = e.message
  }
}

let cursor = 0
let gapKey = 0
let timer = null
let alive = false
let stick = true
let lastScrollTop = 0

const levelOptions = computed(() => [
  { value: 'debug', label: t('logs.level.all') },
  { value: 'info', label: t('logs.level.info') },
  { value: 'warn', label: t('logs.level.warn') },
  { value: 'error', label: t('logs.level.error') }
])
const visible = computed(() => lines.value.filter((l) => l.gap || RANK[l.level] >= RANK[minLevel.value]))

const body = () => terminal.value?.$el?.querySelector('.noah-terminal-body')
const clock = (utc) => new Date(utc).toLocaleTimeString([], { hour12: false })

function onTerminalScroll(event) {
  const element = body()
  if (event.target !== element) return
  if (element.scrollHeight - element.scrollTop - element.clientHeight <= 2) stick = true
  else if (element.scrollTop < lastScrollTop - 1) stick = false
  lastScrollTop = element.scrollTop
}

async function followOutput() {
  await nextTick()
  const element = body()
  if (!stick || !element) return
  element.scrollTop = element.scrollHeight
  lastScrollTop = element.scrollTop
}
watch(minLevel, followOutput)

async function poll() {
  try {
    const res = await api('logs?after=' + cursor)
    // A cursor that ran ahead of the server means it restarted: start over with its lines.
    if (res.next < cursor) { lines.value = []; cursor = 0 }
    if (res.entries.length) {
      const fresh = res.entries.map((e) => ({ ...e }))
      if (cursor > 0 && fresh[0].seq > cursor + 1) lines.value.push({ gap: fresh[0].seq - cursor - 1, seq: `gap${++gapKey}` })
      lines.value.push(...fresh)
      if (lines.value.length > MAX_LINES) lines.value.splice(0, lines.value.length - MAX_LINES)
    }
    cursor = res.next
    error.value = null
    loaded.value = true
    await followOutput()
  } catch (e) {
    if (!loaded.value) error.value = e
  }
}

function schedule() {
  clearTimeout(timer)
  if (!alive || restartState.value === 'waiting') return
  timer = setTimeout(async () => {
    if (!document.hidden && !paused.value) await poll()
    schedule()
  }, POLL_MS)
}

function togglePause() {
  paused.value = !paused.value
  if (!paused.value) { clearTimeout(timer); poll().finally(schedule) }
}

function clear() { lines.value = []; commandLines.value = []; stick = true; lastScrollTop = 0 }
function retry() { error.value = null; poll().finally(schedule) }
function onVisibility() { if (!document.hidden && !paused.value) { clearTimeout(timer); poll().finally(schedule) } }

onMounted(() => {
  alive = true
  api('commands').then(value => { commands.value = value }).catch(e => { actionError.value = e.message })
  api('maintenance').then(value => { maintenance.value = value.enabled; maintenanceAvailable.value = true }).catch(e => { actionError.value = e.message })
  poll().finally(schedule)
  document.addEventListener('visibilitychange', onVisibility)
  document.addEventListener('keydown', onEscape)
})
onUnmounted(() => {
  alive = false
  clearTimeout(timer)
  document.removeEventListener('visibilitychange', onVisibility)
  document.removeEventListener('keydown', onEscape)
  if (maximized.value) document.body.style.overflow = savedBodyOverflow
})
</script>

<template>
  <Teleport to="body" :disabled="!maximized">
  <ErrorCard v-if="error && !loaded" :error="error" @retry="retry" />
  <SectionCard v-else-if="!loaded">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <!-- The overview subtitle is retained as console.sub without occupying console space. -->
  <SectionCard v-else ref="consoleCard" :class="{ 'overview-console': interactive, 'console-maximized': maximized }" :title="t(interactive ? 'console.title' : 'nav.logs')" :sub="interactive ? undefined : t('logs.sub')">
    <template #extra>
      <div class="log-tools">
        <div v-if="interactive" class="console-maintenance" :title="t('console.maintenanceHint')"><strong>{{ t('console.maintenance') }}</strong><NoahSwitch :model-value="maintenance" :disabled="!maintenanceAvailable || savingMaintenance || restartState === 'waiting'" :aria-label="t('console.maintenance')" @update:model-value="toggleMaintenance" /></div>
        <NoahButton v-if="interactive" variant="glass" @click="previewInfoOpen = true">{{ t('console.preview') }}</NoahButton>
        <div class="log-level"><NoahSelect v-model="minLevel" :options="levelOptions" /></div>
        <NoahButton variant="glass" :icon="paused ? 'play' : 'pause'" @click="togglePause">{{ paused ? t('logs.resume') : t('logs.pause') }}</NoahButton>
        <NoahButton variant="glass" icon="trash" @click="clear">{{ t('logs.clear') }}</NoahButton>
        <NoahButton v-if="interactive" class="console-restart" variant="glass" icon="refresh" @click="restartConfirmOpen = true">{{ t('console.restart') }}</NoahButton>
      </div>
    </template>
    <NoahTerminal ref="terminal" class="log-terminal" :class="{ 'console-terminal': interactive }" :title="interactive ? 'Bubble Fighter Fantasy — console' : t('logs.terminal')" :controls="interactive" :copyable="false" @close="clear" @scroll.capture="onTerminalScroll" :expanded="interactive ? maximized : undefined" :maximize-label="t(maximized ? 'console.restore' : 'console.maximize')" @maximize="setMaximized" @minimize="followOutput">
      <div v-if="!visible.length" class="log-line is-gap">{{ t('logs.empty') }}</div>
      <div v-for="line in visible" :key="line.seq" class="log-line" :class="line.gap ? 'is-gap' : 'is-' + line.level">
        <template v-if="line.gap">{{ t('logs.gap', { count: line.gap }) }}</template>
        <template v-else>
          <span class="log-time">{{ clock(line.utc) }}</span>
          <span class="log-level-tag">{{ line.level.toUpperCase() }}</span>
          <span class="log-cat">{{ line.category }}</span>
          <span class="log-msg">{{ line.message }}</span>
        </template>
      </div>
      <div v-for="entry in commandLines" :key="'command-' + entry.id" class="console-command-output"><div><span class="console-prompt">&gt;</span> {{ entry.command }}</div><div class="console-command-result">{{ entry.message }}</div></div>
    </NoahTerminal>
    <template v-if="interactive">
      <form class="console-command-form" @submit.prevent="submitCommand()">
        <NoahInput v-model="command" prefix-icon="terminal" :placeholder="t('console.commandPlaceholder')" :aria-label="t('console.commandLabel')" maxlength="1024" autocomplete="off" />
        <NoahButton variant="primary" html-type="submit" :disabled="!command.trim()">{{ t('console.send') }}</NoahButton>
      </form>
      <p v-if="restartState === 'waiting'" class="console-operation-state" role="status">{{ t('console.restartProgress') }}</p>
      <p v-else-if="restartState === 'success'" class="console-operation-state is-success" role="status">{{ t('console.restartSuccess') }}</p>
      <p v-if="actionError" class="console-operation-state is-error" role="alert">{{ actionError }}</p>
      <div class="console-shortcuts">
        <span class="text-caption text-muted">{{ t('console.quickCommands') }}</span>
        <NoahButton v-for="item in quickCommands" :key="item.command" variant="glass" :icon="item.icon" :title="item.command" @click="submitCommand(item.command)">{{ item.label }}</NoahButton>
      </div>
    </template>
  </SectionCard>
  </Teleport>
  <NoahModal v-model="restartConfirmOpen" :title="t('console.restartTitle')" :close-label="t('common.close')" width="420px">
    <p>{{ t('console.restartConfirm') }}</p>
    <template #footer><NoahButton variant="glass" @click="restartConfirmOpen = false">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" :disabled="restartState === 'waiting'" @click="restartServer">{{ t('common.confirm') }}</NoahButton></template>
  </NoahModal>
  <NoahModal v-model="previewInfoOpen" :title="t('console.preview')" :close-label="t('common.close')" width="420px">
    <p>{{ t('console.previewResult') }}</p>
    <template #footer><NoahButton variant="primary" @click="previewInfoOpen = false">{{ t('common.close') }}</NoahButton></template>
  </NoahModal>
</template>

<style scoped>
.console-terminal :deep(.noah-terminal-body) { height: clamp(260px, 38vh, 460px); max-height: none; overflow-y: auto; }

.console-terminal :deep(.noah-terminal-body) { scrollbar-color: #555 #171717; scrollbar-width: thin; }
.console-terminal :deep(.noah-terminal-body::-webkit-scrollbar-thumb) { background: #555; border-radius: 999px; }
.console-terminal :deep(.noah-terminal-body::-webkit-scrollbar-track),
.console-terminal :deep(.noah-terminal-body::-webkit-scrollbar-corner) { background: #171717; }
.console-terminal :deep(.noah-terminal-bar) { padding: 12px 16px; }
.console-command-form { display: flex; gap: 10px; margin-top: 16px; }
.console-command-form > .noah-input-wrap { flex: 1; min-width: 0; }
.console-shortcuts { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 16px 0; }
.console-shortcuts > span { margin-right: 4px; }
.console-restart { margin-left: auto; }
.console-maintenance { display: flex; align-items: center; gap: 12px; min-height: 32px; margin-right: 4px; }
.console-maintenance strong { font-size: var(--text-control); font-weight: 500; }
.log-tools :deep(.noah-btn) { min-height: 32px; }
.log-tools .log-level :deep(.noah-select .noah-select-trigger) { height: 32px; font-size: var(--text-control); }
.console-command-output { white-space: pre-wrap; overflow-wrap: anywhere; margin-top: 8px; }
.console-prompt { color: #7aa2ff; }
.console-command-result { color: #b9c7d8; white-space: pre-wrap; }
.console-operation-state { margin:12px 0 0; color:var(--text-muted); font-size:var(--text-caption); }
.console-operation-state.is-success { color:var(--green); }
.console-operation-state.is-error { color:var(--red); overflow-wrap:anywhere; }
@media (min-width: 1100px) {
  .overview-console { flex: 1; min-height: 300px; display: flex; flex-direction: column; }
  .overview-console :deep(.noah-card-body) { flex: 1; min-height: 0; display: flex; flex-direction: column; }
  .overview-console :deep(.section-head) { flex: none; }
  .overview-console .console-terminal { flex: 1; min-height: 120px; display: flex; flex-direction: column; }
  .overview-console .console-terminal :deep(.noah-terminal-bar) { flex: none; }
  .overview-console .console-terminal :deep(.noah-terminal-body) { flex: 1; min-height: 0; height: 0; }
  .overview-console .console-command-form { flex: none; margin-top: 12px; }
  .overview-console .console-shortcuts { flex: none; padding: 12px 0 0; }
}
.console-maximized.overview-console { position: fixed; inset: 0; z-index: 900; width: 100%; height: 100dvh; margin: 0; border-radius: 0; min-height: 0; display: flex; flex-direction: column; background: var(--bg); box-sizing: border-box; border: 0; box-shadow: none; transition: none; }
.console-maximized :deep(.noah-card-body) { flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: auto; }
.console-maximized :deep(.section-head) { flex: none; }
.console-maximized .console-terminal { box-shadow: none; flex: 1; min-height: 100px; display: flex; flex-direction: column; }
.console-maximized .console-terminal :deep(.noah-terminal-bar) { flex: none; }
.console-maximized .console-terminal :deep(.noah-terminal-body) { flex: 1; min-height: 0; height: 0; max-height: none; }
.console-maximized .console-terminal.is-collapsed { flex: none; min-height: 0; }
.console-maximized .console-command-form { flex: none; margin-top: 12px; }
.console-maximized .console-shortcuts { flex: none; padding: 12px 0 0; }
@media (max-width: 560px) { .console-command-form { gap: 8px; } .console-shortcuts > span { width: 100%; } }
</style>
