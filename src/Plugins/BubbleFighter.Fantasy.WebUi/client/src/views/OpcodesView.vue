<script setup>
import { computed, ref, watch } from 'vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

// The table is rendered in full only up to this many rows; a filter narrows it to the rest.
const MAX_ROWS = 200

const { t, te } = useI18n()
// The overview lists every served build with its release name, which is what the selector shows.
const builds = useResource(() => api('overview'))
const build = ref('')
const filter = ref('')
const table = useResource(() => api('opcodes?profile=' + encodeURIComponent(build.value)), { immediate: false })

watch(builds.data, (overview) => {
  const list = overview?.builds ?? []
  if (list.length && !build.value) build.value = (list.find((b) => b.id === 'kr-1256') ?? list[0]).id
})
watch(build, (id) => { if (id) table.reload() })

const buildOptions = computed(() => (builds.data.value?.builds ?? []).map((b) => ({ value: b.id, label: b.release ? `${b.release} (${b.id})` : b.id })))
const entries = computed(() => table.data.value?.find((e) => e.profile === build.value)?.opcodes ?? [])
const matches = computed(() => {
  const needle = filter.value.trim().toLowerCase()
  if (!needle) return entries.value
  return entries.value.filter((o) => String(o.value).includes(needle) || o.name.toLowerCase().includes(needle))
})

const columns = computed(() => [
  { key: 'value', title: t('col.value'), sortable: true },
  { key: 'direction', title: t('col.direction') },
  { key: 'connection', title: t('col.connection') },
  { key: 'name', title: t('col.name') },
  { key: 'evidence', title: t('col.evidence') }
])
const label = (prefix, value) => (te(prefix + value) ? t(prefix + value) : value)
const evidenceTone = (evidence) => ({ Verified: 'success', Captured: 'info', Inferred: 'warning' }[evidence] ?? 'default')
const hint = (o) => [o.source, o.notes].filter(Boolean).join(' - ')
</script>

<template>
  <ErrorCard v-if="builds.error.value && !builds.data.value" :error="builds.error.value" @retry="builds.reload" />
  <SectionCard v-else-if="!builds.data.value">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <SectionCard v-else :title="t('nav.opcodes')" :sub="t('opcodes.sub')">
    <template #extra>
      <span v-if="table.data.value" class="text-caption text-muted">{{ t('opcodes.total', { total: entries.length }) }}</span>
    </template>
    <div class="toolbar">
      <div class="toolbar-field toolbar-build">
        <NoahSelect v-model="build" :options="buildOptions" :placeholder="t('opcodes.build')" />
      </div>
      <div class="toolbar-field toolbar-filter">
        <NoahInput v-model="filter" clearable prefix-icon="search" :placeholder="t('opcodes.filter')" />
      </div>
    </div>
    <ErrorCard v-if="table.error.value && !table.data.value" :error="table.error.value" @retry="table.reload" />
    <template v-else>
      <div class="table-wrap">
        <NoahTable :columns="columns" :data="matches" :limit="MAX_ROWS" :default-sort="{ key: 'value', order: 'asc' }" :loading="table.loading.value" :empty-text="t('opcodes.empty')" :loading-text="t('common.loading')">
          <template #value="{ row }"><span class="mono">{{ row.value }}</span></template>
          <template #direction="{ row }"><span class="text-muted">{{ label('dir.', row.direction) }}</span></template>
          <template #connection="{ row }">{{ label('connection.', row.connection) }}</template>
          <template #name="{ row }"><span class="mono" :title="hint(row)">{{ row.name }}</span></template>
          <template #evidence="{ row }">
            <NoahTag pill size="sm" :tone="evidenceTone(row.evidence)">{{ label('evidence.', row.evidence) }}</NoahTag>
          </template>
        </NoahTable>
      </div>
      <p v-if="matches.length > MAX_ROWS" class="table-note text-caption text-muted">
        {{ t('opcodes.showing', { shown: MAX_ROWS, total: matches.length }) }}
      </p>
    </template>
  </SectionCard>
</template>
