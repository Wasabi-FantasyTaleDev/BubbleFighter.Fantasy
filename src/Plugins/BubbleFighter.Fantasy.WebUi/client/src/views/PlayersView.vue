<script setup>
import { computed, watch } from 'vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { useServerStore } from '../stores/server'

const { t, te } = useI18n()
const server = useServerStore()
const { data, error, loading, reload } = useResource(() => api('sessions'), { interval: 5000 })

// Match connections belong to a player who already has a lobby connection, so they do not count.
watch(data, (sessions) => { if (sessions) server.players = sessions.filter((s) => !s.matchBound).length })

const columns = computed(() => [
  { key: 'id', title: t('col.id') },
  { key: 'profile', title: t('col.build') },
  { key: 'player', title: t('col.player') },
  { key: 'phase', title: t('col.phase') },
  { key: 'remote', title: t('col.remote') }
])
const phaseName = (phase) => (te('phase.' + phase) ? t('phase.' + phase) : phase)
</script>

<template>
  <ErrorCard v-if="error && !data" :error="error" @retry="reload" />
  <SectionCard v-else-if="!data || loading && !data">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <SectionCard v-else :title="t('players.title')" :sub="t('players.sub')">
    <div class="table-wrap">
      <NoahTable :columns="columns" :data="data" :empty-text="t('players.empty')" :loading-text="t('common.loading')">
        <template #id="{ row }"><span class="mono">{{ row.id }}</span></template>
        <template #profile="{ row }"><span class="mono">{{ row.profile }}</span></template>
        <template #player="{ row }">
          <template v-if="row.player">
            <span class="cell-strong">{{ row.player.nickname }}</span>
            <span class="cell-sub">{{ row.player.username }} · {{ t('players.level', { level: row.player.level }) }}</span>
          </template>
          <NoahTag v-else-if="row.matchBound" size="sm" pill tone="info">{{ t('players.match') }}</NoahTag>
          <span v-else class="text-muted">{{ t('players.anonymous') }}</span>
        </template>
        <template #phase="{ row }">
          <NoahTag v-if="row.player" size="sm" pill>{{ phaseName(row.player.phase) }}</NoahTag>
          <span v-else class="text-muted">-</span>
        </template>
        <template #remote="{ row }"><span class="mono">{{ row.remote }}</span></template>
      </NoahTable>
    </div>
  </SectionCard>
</template>
