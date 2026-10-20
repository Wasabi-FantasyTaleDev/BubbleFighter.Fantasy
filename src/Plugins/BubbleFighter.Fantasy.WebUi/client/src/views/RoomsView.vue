<script setup>
import { computed } from 'vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

const { t, te } = useI18n()
const { data, error, loading, reload } = useResource(() => api('rooms'), { interval: 5000 })

const columns = computed(() => [
  { key: 'roomId', title: t('col.room') },
  { key: 'seats', title: t('col.seats'), align: 'right' },
  { key: 'phase', title: t('col.phase') }
])
const phaseName = (phase) => (te('roomphase.' + phase) ? t('roomphase.' + phase) : phase)
const phaseTone = (phase) => ({ Playing: 'success', Starting: 'warning', Result: 'info', Closed: 'default' }[phase] ?? 'default')
</script>

<template>
  <ErrorCard v-if="error && !data" :error="error" @retry="reload" />
  <SectionCard v-else-if="!data || loading && !data">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <SectionCard v-else :title="t('rooms.title')" :sub="t('rooms.sub')">
    <div class="table-wrap">
      <NoahTable :columns="columns" :data="data" :empty-text="t('rooms.empty')" :loading-text="t('common.loading')">
        <template #roomId="{ row }"><span class="mono">{{ row.roomId }}</span></template>
        <template #seats="{ row }"><span class="mono">{{ row.seats }}</span></template>
        <template #phase="{ row }">
          <NoahTag pill size="sm" :tone="phaseTone(row.phase)">{{ phaseName(row.phase) }}</NoahTag>
        </template>
      </NoahTable>
    </div>
  </SectionCard>
</template>
