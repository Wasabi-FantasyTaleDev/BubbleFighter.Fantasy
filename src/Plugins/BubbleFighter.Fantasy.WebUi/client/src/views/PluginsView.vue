<script setup>
import { computed, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import { useNoahToast } from '../noah/components/ui/useNoahToast'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

// The row that serves this page cannot reload itself from here: the request would cut its own connection.
const SELF_PLUGIN = 'admin.http'

const { t, te } = useI18n()
const toast = useNoahToast()
const { data, error, loading, reload } = useResource(() => api('plugins'), { interval: 5000 })

const columns = computed(() => [
  { key: 'rowId', title: t('col.row') },
  { key: 'plugin', title: t('col.plugin') },
  { key: 'source', title: t('col.source') },
  { key: 'state', title: t('col.status') },
  { key: 'version', title: t('col.version') },
  { key: 'error', title: t('col.error') },
  { key: 'actions', title: t('col.actions'), align: 'right' }
])

const stateName = (state) => (te('state.' + state) ? t('state.' + state) : state)
const stateTone = (state) => {
  if (state === 'Active') return 'success'
  if (['Failed', 'CleanupFailed', 'WorkFailed'].includes(state)) return 'danger'
  if (['Pending', 'Unloading', 'CleanupIncomplete'].includes(state)) return 'warning'
  return 'default'
}

// A version reads `1.0.0+<commit>`; the table shows the first 7 hex characters of the commit and the title keeps the whole string.
function shortVersion(version) {
  if (!version) return '-'
  const plus = version.indexOf('+')
  return plus < 0 ? version : version.slice(0, plus + 1) + version.slice(plus + 1, plus + 8)
}

const target = ref(null)
const reloading = ref(false)
const confirmOpen = computed({
  get: () => target.value !== null,
  set: (open) => { if (!open && !reloading.value) target.value = null }
})

async function confirmReload() {
  const row = target.value
  reloading.value = true
  try {
    const result = await api(`plugins/${encodeURIComponent(row.rowId)}/reload`, { method: 'POST' })
    toast.ok(t('plugins.reload.done', { rows: result.reloadedRowIds.join(', ') }), t('common.saved'))
  } catch (e) {
    toast.err(t('plugins.reload.failed', { row: row.rowId, reason: e.message }), t('common.error'))
  } finally {
    reloading.value = false
    target.value = null
    reload()
  }
}
</script>

<template>
  <ErrorCard v-if="error && !data" :error="error" @retry="reload" />
  <SectionCard v-else-if="!data || loading && !data">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <SectionCard v-else :title="t('plugins.title')" :sub="t('plugins.sub')">
    <div class="table-wrap">
      <NoahTable :columns="columns" :data="data" :empty-text="t('plugins.empty')" :loading-text="t('common.loading')">
        <template #rowId="{ row }"><span class="cell-strong mono">{{ row.rowId }}</span></template>
        <template #plugin="{ row }"><span class="mono">{{ row.plugin }}</span></template>
        <template #source="{ row }"><span class="text-muted">{{ row.source }}</span></template>
        <template #state="{ row }">
          <NoahTag pill size="sm" :tone="stateTone(row.state)">{{ stateName(row.state) }}</NoahTag>
        </template>
        <template #version="{ row }"><span class="mono" :title="row.version ?? ''">{{ shortVersion(row.version) }}</span></template>
        <template #error="{ row }">
          <span v-if="row.error" class="cell-error" :title="row.error">{{ row.error }}</span>
          <span v-else class="text-muted">-</span>
        </template>
        <template #actions="{ row }">
          <NoahButton
            size="xs"
            icon="refresh"
            :disabled="row.plugin === SELF_PLUGIN"
            :title="row.plugin === SELF_PLUGIN ? t('plugins.reload.self') : ''"
            @click="target = row"
          >{{ t('plugins.reload') }}</NoahButton>
        </template>
      </NoahTable>
    </div>
  </SectionCard>

  <NoahModal v-model="confirmOpen" :title="target ? t('plugins.reload.title', { row: target.rowId }) : ''" width="460px" :close-label="t('common.close')">
    <p class="confirm-text">{{ t('plugins.reload.body') }}</p>
    <template #footer>
      <NoahButton size="sm" :disabled="reloading" @click="target = null">{{ t('common.cancel') }}</NoahButton>
      <NoahButton size="sm" variant="primary" :loading="reloading" @click="confirmReload">{{ t('plugins.reload') }}</NoahButton>
    </template>
  </NoahModal>
</template>
