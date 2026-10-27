<script setup>
import { computed, nextTick, onBeforeUnmount, ref, watch, watchEffect } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import PlayerLevelIcon from '../components/PlayerLevelIcon.vue'
import { clientBuildLabel } from '../utils/clientBuildLabel'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

const { t } = useI18n()
const { data, error, loading, reload } = useResource(() => api('rooms'), { interval: 5000 })
const query = ref('')
const mode = ref('')
const passwordId = ref(null)
const revealedPasswords = ref({})
const passwordLoading = ref(null)
const passwordError = ref('')
const selected = ref(null)
const panel = ref(null)
let trigger
const roomName = room => room.titleKey ? t(room.titleKey) : room.title || t('rooms.number', { id: room.roomId })
const modeName = room => room.modeKey ? t(room.modeKey) : room.modeName || room.mode || '—'
const ownerName = room => (typeof room.owner === 'string' ? room.owner : room.owner?.nickname) || room.ownerName || '—'
const hasStarted = room => ['Starting', 'Playing', 'Result'].includes(room.phase)
const hasState = room => room.phase === 'Waiting' || hasStarted(room)
const roomVersion = room => room.clientBuild ? clientBuildLabel(room.clientBuild) : room.build || '—'
const mapName = room => room.mapKey ? t(room.mapKey) : room.mapName || room.map || '—'
const occupied = room => room.playerCount ?? room.players?.length ?? (Array.isArray(room.seats) ? room.seats.length : room.seats) ?? 0
const occupancy = room => `${occupied(room)}${room.capacity != null ? ' / ' + room.capacity : ''}`
const rows = computed(() => (data.value || []).map(room => ({ ...room, id: room.roomId })))
const modes = computed(() => [{ value: '', label: t('rooms.allModes') }, ...[...new Set(rows.value.map(room => modeName(room)))].filter(value => value !== '—').map(value => ({ value, label: value }))])
const filtered = computed(() => rows.value.filter(room => (!mode.value || modeName(room) === mode.value) && [roomName(room), ownerName(room), room.roomId, modeName(room)].some(value => String(value).toLocaleLowerCase().includes(query.value.trim().toLocaleLowerCase()))))
const current = computed(() => rows.value.find(room => room.roomId === selected.value))
const open = computed(() => !!current.value)
const columns = computed(() => [
  { key: 'mode', title: t('rooms.mode'), width: '12%' },
  { key: 'owner', title: t('rooms.owner'), width: '14%' },
  { key: 'title', title: t('rooms.roomTitle') },
  { key: 'state', title: t('rooms.state'), width: '96px' },
  { key: 'build', title: t('rooms.version'), width: '22%' },
  { key: 'count', title: t('rooms.occupancy'), width: '80px', align: 'right' },
  { key: 'details', title: '', width: '48px' }
])
const teams = computed(() => ['red', 'blue', 'spectator'].map(id => ({ id, label: t('rooms.team.' + id), players: (current.value?.players || []).filter(player => player.team === (id === 'blue' ? 1 : id === 'red' ? 2 : 0)) })).filter(team => team.players.length))
function show(room, event) { trigger = event.currentTarget; selected.value = room.roomId }
function close() { selected.value = null }
async function togglePassword(room) {
  passwordError.value = ''
  if (passwordId.value === room.roomId) { passwordId.value = null; return }
  if (Object.hasOwn(revealedPasswords.value, room.roomId)) { passwordId.value = room.roomId; return }
  passwordLoading.value = room.roomId
  try {
    const details = await api(`rooms/${encodeURIComponent(room.clientBuild || '')}/${room.roomId}`)
    revealedPasswords.value = { ...revealedPasswords.value, [room.roomId]: details.password ?? '' }
    passwordId.value = room.roomId
  } catch (e) { passwordError.value = e.message }
  finally { passwordLoading.value = null }
}
watch(open, async value => { await nextTick(); if (value) panel.value?.querySelector('button')?.focus(); else if (trigger?.isConnected) trigger.focus() })
watchEffect(onCleanup => {
  if (!open.value) return
  const previous = document.body.style.overflow
  document.body.style.overflow = 'hidden'
  onCleanup(() => { document.body.style.overflow = previous })
})
function keydown(event) {
  if (event.key === 'Escape') { event.preventDefault(); close(); return }
  if (event.key !== 'Tab') return
  const buttons = [...panel.value.querySelectorAll('button:not(:disabled), a[href], input, [tabindex="0"]')].filter(element => element.getClientRects().length)
  const first = buttons[0], last = buttons.at(-1)
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
}
onBeforeUnmount(() => { if (trigger?.isConnected) trigger.focus() })
</script>

<template>
  <section class="rooms-page" :inert="open || undefined">
    <header class="rooms-toolbar">
      <strong>{{ t('rooms.directory') }}</strong>
      <div class="rooms-tools"><span class="text-caption text-muted">{{ t('rooms.count', { shown: filtered.length, total: rows.length }) }}</span><NoahInput v-model="query" prefix-icon="search" :placeholder="t('rooms.search')" :aria-label="t('rooms.search')" /><NoahSelect v-model="mode" :options="modes" :aria-label="t('rooms.mode')" /><NoahButton variant="glass" icon="refresh" icon-only :loading="loading" :title="t('store.refresh')" :aria-label="t('store.refresh')" @click="reload" /></div>
    </header>
    <ErrorCard v-if="error" :error="error" @retry="reload" />
    <p v-if="passwordError" class="room-password-error" role="alert">{{ passwordError }}</p>
    <div class="rooms-table"><NoahTable :columns="columns" :data="filtered" :loading="loading && !data" :empty-text="t(query || mode ? 'store.noMatches' : 'rooms.empty')" :loading-text="t('common.loading')">
      <template #mode="{ row }"><span class="room-cell text-muted" :title="modeName(row)">{{ modeName(row) }}</span></template>
      <template #owner="{ row }"><span class="room-cell" :title="ownerName(row)">{{ ownerName(row) }}</span></template>
      <template #title="{ row }"><span class="room-title-cell"><span class="room-cell cell-strong" :title="roomName(row)">{{ roomName(row) }}</span><button v-if="row.locked" type="button" class="room-password-button" :disabled="passwordLoading === row.roomId" :aria-label="t('rooms.showPassword', { name: roomName(row) })" :title="t('rooms.password')" :aria-pressed="passwordId === row.roomId" @click="togglePassword(row)"><NoahIcon name="lock" :size="14" /></button><span v-if="row.locked && passwordId === row.roomId" class="room-password-value">{{ revealedPasswords[row.roomId] || '—' }}</span></span></template>
      <template #state="{ row }"><NoahTag v-if="hasState(row)" pill size="sm" :tone="hasStarted(row) ? 'info' : 'success'">{{ t(hasStarted(row) ? 'rooms.started' : 'rooms.waiting') }}</NoahTag><span v-else class="text-muted">—</span></template>
      <template #build="{ row }"><span class="room-cell text-muted" :title="roomVersion(row)">{{ roomVersion(row) }}</span></template>
      <template #count="{ row }"><span class="room-occupancy">{{ occupancy(row) }}</span></template>
      <template #details="{ row }"><button type="button" class="room-open" :aria-label="t('rooms.openDetails', { name: roomName(row) })" :title="t('rooms.details')" @click="show(row, $event)"><NoahIcon name="chevron-right" :size="18" /></button></template>
    </NoahTable></div>
  </section>
  <Teleport to="body"><Transition name="room-drawer"><div v-if="open" class="room-drawer-overlay" @click.self="close" @keydown="keydown">
    <aside ref="panel" class="room-drawer-panel" role="dialog" aria-modal="true" aria-labelledby="room-drawer-title">
      <header class="room-drawer-heading"><div><span class="text-caption text-muted">{{ t('rooms.number', { id: current.roomId }) }}</span><div class="room-heading-title"><h2 id="room-drawer-title">{{ roomName(current) }}</h2><NoahTag v-if="hasState(current)" pill size="sm" :tone="hasStarted(current) ? 'info' : 'success'">{{ t(hasStarted(current) ? 'rooms.started' : 'rooms.waiting') }}</NoahTag></div></div><NoahButton variant="glass" icon="close" icon-only :aria-label="t('common.close')" @click="close" /></header>
      <div class="room-drawer-content">
        <dl class="room-facts"><dt>{{ t('rooms.mode') }}</dt><dd>{{ modeName(current) }}</dd><dt>{{ t('rooms.owner') }}</dt><dd>{{ ownerName(current) }}</dd><dt>{{ t('rooms.map') }}</dt><dd>{{ mapName(current) }}</dd><dt>{{ t('rooms.occupancy') }}</dt><dd>{{ occupancy(current) }}</dd><!-- Room status is shown beside the room title. <dt>{{ t('rooms.state') }}</dt><dd><NoahTag v-if="hasState(current)" pill size="sm" :tone="hasStarted(current) ? 'info' : 'success'">{{ t(hasStarted(current) ? 'rooms.started' : 'rooms.waiting') }}</NoahTag><span v-else>—</span></dd> --><dt>{{ t('rooms.version') }}</dt><dd>{{ roomVersion(current) }}</dd></dl>
        <div class="room-player-heading"><h3>{{ t('rooms.playerList') }}</h3><span class="text-caption text-muted">{{ t('rooms.playerCount', { count: occupied(current) }) }}</span></div>
        <template v-if="current.players?.length">
          <section v-for="team in teams" :key="team.id" class="room-team" :class="'team-' + team.id"><header><span class="team-dot" /><h4>{{ team.label }}</h4><span class="text-caption text-muted">{{ team.players.length }}</span></header>
            <div v-for="player in team.players" :key="player.id" class="room-player"><PlayerLevelIcon :level="player.level" /><div class="room-player-copy"><strong>{{ player.nickname }}</strong><span>Lv.{{ player.level }}</span></div><div class="room-player-tags"><NoahTag v-if="player.host" pill size="sm" tone="warning">{{ t('rooms.owner') }}</NoahTag><NoahTag v-if="player.ai" pill size="sm" tone="info">AI</NoahTag><NoahTag v-if="!player.host" pill size="sm" :tone="player.ready ? 'success' : 'default'">{{ t(player.ready ? 'rooms.ready' : 'rooms.notReady') }}</NoahTag></div></div>
          </section>
        </template>
        <p v-else class="room-player-empty text-muted">{{ t('rooms.playersUnavailable') }}</p>
      </div>
      <footer class="room-drawer-footer"><NoahButton variant="glass" @click="close">{{ t('common.close') }}</NoahButton></footer>
    </aside>
  </div></Transition></Teleport>
</template>

<style scoped>
.rooms-page { display:flex; flex-direction:column; gap:20px; min-width:0; }
.rooms-toolbar, .rooms-tools { display:flex; align-items:center; gap:12px; flex-wrap:wrap; }
.rooms-toolbar { justify-content:space-between; }
.rooms-toolbar > strong { font-size:var(--text-body); font-weight:500; }
.rooms-tools { margin-left:auto; }
.rooms-tools > span { white-space:nowrap; }
.rooms-tools > .noah-input-wrap { width:250px; }
.rooms-tools > .noah-select { width:150px; }
.rooms-table { min-width:0; }
.rooms-table :deep(.noah-table-container) { background:transparent; border:0; border-radius:0; }
.rooms-table :deep(.noah-tag), .room-facts :deep(.noah-tag) { padding:5px 9px; line-height:1.2; white-space:nowrap; }
.rooms-table :deep(.noah-table) { table-layout:fixed; }
.rooms-table :deep(th) { background:transparent; font-weight:500; }
.rooms-table :deep(th), .rooms-table :deep(td) { padding:12px 14px; }
.rooms-table :deep(td:last-child) { padding:6px; }
.room-cell { display:block; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.room-title-cell { display:flex; align-items:center; gap:8px; min-width:0; }
.room-password-button { display:grid; place-items:center; flex:none; width:26px; height:26px; border:0; padding:0; border-radius:var(--radius-xs); color:var(--text-muted); background:transparent; cursor:pointer; }
.room-password-button:hover { color:var(--text); background:var(--hover); }
.room-password-button:focus-visible { outline:2px solid var(--accent-ring); outline-offset:1px; }
.room-password-value { flex:none; max-width:45%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:var(--text-control); font-family:var(--font-ui); font-variant-numeric:tabular-nums; }
.room-password-error { margin:0; color:var(--red); font-size:var(--text-caption); }
.room-occupancy { white-space:nowrap; font-variant-numeric:tabular-nums; }
.room-open { display:grid; place-items:center; width:32px; height:32px; padding:0; border:0; border-radius:var(--radius-xs); background:transparent; color:var(--text-muted); cursor:pointer; transition:background-color 160ms,color 160ms; }
.room-open:hover { color:var(--text); background:var(--hover); }
.room-open:focus-visible { outline:2px solid var(--accent-ring); outline-offset:1px; }
.room-drawer-overlay { position:fixed; inset:0; z-index:1000; display:flex; justify-content:flex-end; background:rgb(0 0 0 / .22); }
.room-drawer-panel { display:flex; flex-direction:column; width:min(420px,100%); height:100dvh; min-width:0; background:var(--surface); box-shadow:var(--shadow-lg); }
.room-drawer-heading { display:flex; align-items:flex-start; justify-content:space-between; gap:16px; padding:24px; border-bottom:1px solid var(--border-soft); flex:none; }
.room-drawer-heading > div { min-width:0; }
.room-heading-title { display:flex; align-items:center; flex-wrap:wrap; gap:10px; margin-top:8px; }
.room-heading-title :deep(.noah-tag) { flex:none; padding:5px 9px; line-height:1.2; white-space:nowrap; }
.room-drawer-heading h2 { margin:0; font-size:22px; line-height:1.5; overflow-wrap:anywhere; }
.room-drawer-content { flex:1; min-height:0; overflow-y:auto; padding:24px; overscroll-behavior:contain; }
.room-facts { display:grid; grid-template-columns:auto minmax(0,1fr); gap:14px; margin:0 0 28px; font-size:var(--text-control); }
.room-facts dt { color:var(--text-muted); }
.room-facts dd { margin:0; text-align:right; overflow-wrap:anywhere; }
.room-player-heading, .room-team > header { display:flex; align-items:center; gap:8px; }
.room-player-heading { justify-content:space-between; margin-bottom:16px; }
.room-player-heading h3 { font-size:var(--text-body); margin:0; }
.room-team { margin-top:20px; }
.room-team > header { margin-bottom:10px; }
.room-team h4 { font-size:var(--text-control); font-weight:500; margin:0; }
.room-team > header > span:last-child { margin-left:auto; }
.team-dot { width:7px; height:7px; border-radius:50%; background:var(--text-faint); }
.team-red .team-dot { background:#e46363; }
.team-blue .team-dot { background:#5886df; }
.room-player { display:flex; align-items:center; gap:12px; padding:14px 0; border-bottom:1px solid var(--border-soft); }
.room-player-copy { display:flex; align-items:center; gap:8px; min-width:0; flex:1; }
.room-player-copy strong { font-size:var(--text-control); font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.room-player-copy > span { flex:none; white-space:nowrap; font-size:var(--text-caption); color:var(--text-muted); }
.room-player-tags { display:flex; align-items:center; flex-wrap:wrap; justify-content:flex-end; gap:6px; max-width:140px; }
.room-player-tags :deep(.noah-tag) { padding:5px 9px; line-height:1.2; }
.room-player-empty { font-size:var(--text-control); line-height:1.8; }
.room-drawer-footer { display:flex; justify-content:flex-end; flex:none; padding:16px 24px; border-top:1px solid var(--border-soft); }
.room-drawer-enter-active, .room-drawer-leave-active { transition:background-color 240ms; }
.room-drawer-enter-active .room-drawer-panel, .room-drawer-leave-active .room-drawer-panel { transition:transform 240ms cubic-bezier(.2,.8,.2,1); }
.room-drawer-enter-from, .room-drawer-leave-to { background:transparent; }
.room-drawer-enter-from .room-drawer-panel, .room-drawer-leave-to .room-drawer-panel { transform:translateX(100%); }
@media(min-width:861px) {
  :global(.shell:has(.rooms-page)) { height:100dvh; min-height:640px; }
  :global(.shell:has(.rooms-page) .main) { min-height:0; }
  :global(.shell:has(.rooms-page) .breadcrumb-bar) { flex:none; }
  :global(.shell:has(.rooms-page) .view) { flex:1; min-height:0; padding-block:28px; }
  :global(.shell:has(.rooms-page) .route-page) { flex:1; min-height:0; }
  :global(.shell:has(.rooms-page) .page-heading) { flex:none; margin-bottom:0; }
  .rooms-page { flex:1; min-height:0; }
  .rooms-toolbar { flex:none; }
  .rooms-table { flex:1; min-height:0; }
  .rooms-table :deep(.noah-table-container) { height:100%; overflow:auto; scrollbar-gutter:stable; }
}
@media(max-width:600px) {
  .rooms-tools { width:100%; }
  .rooms-tools > .noah-input-wrap { flex:1; min-width:160px; width:auto; }
  .rooms-tools > .noah-select { width:130px; }
  .rooms-table :deep(.noah-table) { min-width:760px; }
  .rooms-table :deep(th), .rooms-table :deep(td) { padding:12px 6px; font-size:var(--text-caption); }
  .rooms-table :deep(th:nth-child(6)) { width:60px !important; }
  .rooms-table :deep(th:last-child) { width:38px !important; }
  .room-drawer-heading, .room-drawer-content { padding:20px; }
}
@media(prefers-reduced-motion:reduce) { .room-drawer-enter-active, .room-drawer-leave-active, .room-drawer-enter-active .room-drawer-panel, .room-drawer-leave-active .room-drawer-panel { transition:none; } }
</style>
