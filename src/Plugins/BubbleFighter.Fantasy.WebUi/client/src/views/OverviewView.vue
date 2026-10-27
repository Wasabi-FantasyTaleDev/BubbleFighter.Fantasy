<script setup>
import { computed, watch } from 'vue'
import { RouterLink } from 'vue-router'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import NoahMetricCard from '../noah/components/ui/NoahMetricCard.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import SectionCard from '../components/SectionCard.vue'
import ServerConsole from '../components/ServerConsole.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { useServerStore } from '../stores/server'

const { t, te } = useI18n()
const server = useServerStore()
const { data, error, loading, now, reload } = useResource(() => api('overview'), { interval: 5000 })

watch(data, (overview) => { if (overview) server.players = overview.players })

function uptime(startedUtc) {
  let s = Math.max(0, Math.floor((now.value - Date.parse(startedUtc)) / 1000))
  const d = Math.floor(s / 86400); s -= d * 86400
  const h = Math.floor(s / 3600); s -= h * 3600
  const m = Math.floor(s / 60)
  return [d && t('time.d', { n: d }), (d || h) && t('time.h', { n: h }), t('time.m', { n: m })].filter(Boolean).join(' ')
}

// The rooms count is null when no row publishes the room service; it is shown as a dash then.
const metrics = computed(() => {
  const o = data.value
  if (!o) return []
  // Healthy means active and not faulted; a row disabled by configuration is neither a fault nor healthy.
  const failed = o.rows.failed
  const disabled = o.rows.disabled
  const foot = [failed ? t('overview.rows.foot', { failed }) : t('overview.rows.foot.ok'), disabled ? t('overview.rows.disabled', { disabled }) : '']
    .filter(Boolean).join(', ')
  return [
    { key: 'players', icon: 'person', label: t('overview.players'), value: o.players, foot: t('overview.players.foot') },
    { key: 'rooms', icon: 'rooms', label: t('overview.rooms'), value: o.rooms ?? '-', foot: t('overview.rooms.foot') },
    { key: 'builds', icon: 'builds', label: t('overview.builds'), value: o.builds.length, foot: t('overview.builds.foot') },
    { key: 'plugins', icon: 'plugins', label: t('nav.plugins'), value: o.rows.active, foot, warn: failed > 0 }
  ]
})

const columns = computed(() => [
  { key: 'build', title: t('col.build') },
  { key: 'region', title: t('col.region') },
  { key: 'gamePort', title: t('col.game_port'), align: 'right', sortable: true },
  { key: 'relayPort', title: t('col.relay_port'), align: 'right' },
  { key: 'players', title: t('col.players'), align: 'right' },
  { key: 'status', title: t('col.status') }
])
const regionName = (code) => (te('region.' + code) ? t('region.' + code) : code.toUpperCase())
</script>

<template>
  <ErrorCard v-if="error && !data" :error="error" @retry="reload" />
  <SectionCard v-else-if="!data || loading && !data">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <template v-else>
    <div class="metrics">
      <NoahMetricCard v-for="m in metrics" :key="m.key" :class="{ 'is-warn': m.warn }" :label="m.label" :value="m.value" :subtext="m.foot">
        <template #icon><NoahIcon :name="m.icon" :size="15" /></template>
        <template #action><RouterLink class="metric-jump" :to="'/' + m.key" :aria-label="t('overview.openPage', { page: m.label })" :title="t('overview.openPage', { page: m.label })"><NoahIcon name="chevron-right" :size="18" /></RouterLink></template>
      </NoahMetricCard>
    </div>
    <!-- Client build table is retained for a future overview layout.
    <SectionCard :title="t('overview.builds.title')" :sub="t('overview.builds.sub')">
      <template #extra>
        <span class="text-caption text-muted">{{ t('overview.uptime', { time: uptime(data.startedUtc) }) }}</span>
      </template>
      <div class="table-wrap">
        <NoahTable :columns="columns" :data="data.builds" :default-sort="{ key: 'gamePort', order: 'asc' }" :loading-text="t('common.loading')">
          <template #build="{ row }">
            <span class="cell-strong mono" :title="row.description">{{ row.release || row.id }}</span>
            <span v-if="row.release" class="cell-sub">{{ row.id }}</span>
          </template>
          <template #region="{ row }">{{ regionName(row.region) }}</template>
          <template #gamePort="{ row }"><span class="mono">{{ row.gamePort ?? '-' }}</span></template>
          <template #relayPort="{ row }"><span class="mono">{{ row.relayPort ?? '-' }}</span></template>
          <template #players="{ row }"><span class="mono">{{ row.players }}</span></template>
          <template #status="{ row }">
            <NoahTag pill size="sm" :tone="row.listening ? 'success' : 'danger'">
              {{ row.listening ? t('status.listening') : t('status.stopped') }}
            </NoahTag>
          </template>
        </NoahTable>
      </div>
    </SectionCard>
    -->
    <ServerConsole interactive />
  </template>
</template>

<style scoped>
.metrics :deep(.noah-metric-card) { position: relative; }
.metrics :deep(.noah-metric-footer) { padding-right: 34px; }
.metric-jump { position: absolute; right: 14px; bottom: 14px; display: grid; place-items: center; width: 30px; height: 30px; border: 1px solid var(--border-soft); border-radius: var(--radius-pill); color: var(--text-muted); background: var(--glass-panel); transition: background-color 160ms, color 160ms; }
.metric-jump:hover { color: var(--text); background: var(--hover); }
.metric-jump:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 2px; }
</style>
