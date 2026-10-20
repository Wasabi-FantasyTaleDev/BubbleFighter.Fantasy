<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTerminal from '../noah/components/ui/NoahTerminal.vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useI18n } from '../i18n'

// The view keeps at most this many lines, so a long-open page cannot grow without bound.
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

let cursor = 0
let gapKey = 0
let timer = null
let alive = false
let stick = true

const levelOptions = computed(() => [
  { value: 'debug', label: t('logs.level.all') },
  { value: 'info', label: t('logs.level.info') },
  { value: 'warn', label: t('logs.level.warn') },
  { value: 'error', label: t('logs.level.error') }
])
const visible = computed(() => lines.value.filter((l) => l.gap || RANK[l.level] >= RANK[minLevel.value]))

const body = () => terminal.value?.$el?.querySelector('.noah-terminal-body')
const clock = (utc) => new Date(utc).toLocaleTimeString([], { hour12: false })

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
      stick = !body() || body().scrollHeight - body().scrollTop - body().clientHeight < 40
      await nextTick()
      if (stick && body()) body().scrollTop = body().scrollHeight
    }
    cursor = res.next
    error.value = null
    loaded.value = true
  } catch (e) {
    if (!loaded.value) error.value = e
  }
}

function schedule() {
  clearTimeout(timer)
  if (!alive) return
  timer = setTimeout(async () => {
    if (!document.hidden && !paused.value) await poll()
    schedule()
  }, POLL_MS)
}

function togglePause() {
  paused.value = !paused.value
  if (!paused.value) { clearTimeout(timer); poll().finally(schedule) }
}

function clear() { lines.value = [] }
function retry() { error.value = null; poll().finally(schedule) }
function onVisibility() { if (!document.hidden && !paused.value) { clearTimeout(timer); poll().finally(schedule) } }

onMounted(() => {
  alive = true
  poll().finally(schedule)
  document.addEventListener('visibilitychange', onVisibility)
})
onUnmounted(() => {
  alive = false
  clearTimeout(timer)
  document.removeEventListener('visibilitychange', onVisibility)
})
</script>

<template>
  <ErrorCard v-if="error && !loaded" :error="error" @retry="retry" />
  <SectionCard v-else-if="!loaded">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <SectionCard v-else :title="t('nav.logs')" :sub="t('logs.sub')">
    <template #extra>
      <div class="log-tools">
        <div class="log-level"><NoahSelect v-model="minLevel" size="sm" :options="levelOptions" /></div>
        <NoahButton size="sm" :icon="paused ? 'play' : 'pause'" @click="togglePause">{{ paused ? t('logs.resume') : t('logs.pause') }}</NoahButton>
        <NoahButton size="sm" icon="trash" @click="clear">{{ t('logs.clear') }}</NoahButton>
      </div>
    </template>
    <NoahTerminal ref="terminal" class="log-terminal" :title="t('logs.terminal')" :copyable="false">
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
    </NoahTerminal>
  </SectionCard>
</template>
