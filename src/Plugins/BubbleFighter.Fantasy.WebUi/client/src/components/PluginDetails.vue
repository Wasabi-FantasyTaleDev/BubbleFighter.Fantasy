<script setup>
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import StoreTabs from './StoreTabs.vue'
import { useI18n } from '../i18n'
import { categoryIcon, groupStatus, stateName, stateTone } from './pluginGroups'

// One plugin as the page shows it: its rows on every client build are one group. Actions still act on one row,
// so a plugin that runs on several builds lists them with their own controls.
const props = defineProps({ current: Object, drawer: Boolean })
defineEmits(['reload', 'unload', 'disable', 'load', 'back', 'close'])
const { t, te } = useI18n()
const SELF_PLUGIN = 'admin.http'
const detailPage = ref('overview')
const rows = computed(() => props.current?.rows || [])
const single = computed(() => rows.value.length === 1 ? rows.value[0] : null)
const status = computed(() => groupStatus(rows.value, t))
const storeTarget = computed(() => props.current?.storeEntry?.id ? { path: '/store', query: { plugin: props.current.storeEntry.id } } : null)
const updateVersion = computed(() => props.current?.availableVersion?.trim())
const errors = computed(() => rows.value.filter(row => row.error))
const detailPages = computed(() => [
  { id: 'overview', label: t('plugins.overview') },
  ...(rows.value.length > 1 || rows.value[0]?.build ? [{ id: 'builds', label: t('plugins.buildsTab', { count: rows.value.length }) }] : []),
  { id: 'definition', label: t('plugins.definition') }
])
watch(() => props.current?.plugin, () => { detailPage.value = 'overview' })
watch(detailPages, pages => { if (!pages.some(page => page.id === detailPage.value)) detailPage.value = 'overview' })
const categoryLabel = id => te('plugins.categoryName.' + id) ? t('plugins.categoryName.' + id) : id
function shortVersion(version) {
  if (!version) return '-'
  const plus = version.indexOf('+')
  return plus < 0 ? version : version.slice(0, plus + 1) + version.slice(plus + 1, plus + 8)
}
const displayVersion = version => version?.split('+', 1)[0] || ''
const sources = computed(() => [...new Set(rows.value.map(row => row.source))].join(', '))
</script>

<template>
  <aside class="plugin-detail" :class="{ 'is-drawer': drawer }" :aria-label="t('plugins.details')">
    <template v-if="current">
      <button v-if="drawer" type="button" class="plugin-drawer-close" :aria-label="t('common.close')" @click="$emit('close')"><NoahIcon name="close" :size="18" /></button>
      <NoahButton v-if="!drawer" class="plugin-back" variant="glass" size="sm" icon="chevron-left" @click="$emit('back')">{{ t('plugins.back') }}</NoahButton>
      <header class="plugin-detail-heading">
        <span class="plugin-detail-icon" :data-category="current.category" aria-hidden="true"><NoahIcon :name="categoryIcon(current.category)" :size="24" /></span>
        <div class="plugin-detail-title">
          <h3>{{ current.name }}</h3>
          <span class="plugin-detail-code mono">{{ current.plugin }}</span>
          <div class="plugin-detail-tags">
            <NoahTag pill size="sm" :tone="status.tone">{{ status.label }}</NoahTag>
            <NoahTag v-if="updateVersion" pill size="sm" tone="info">{{ t('plugins.updateAvailable', { from: displayVersion(current.version), to: displayVersion(updateVersion) }) }}</NoahTag>
            <RouterLink v-if="storeTarget" class="plugin-store-link" :to="storeTarget">{{ t('plugins.openStore') }}<NoahIcon name="chevron-right" :size="14" /></RouterLink>
          </div>
        </div>
      </header>
      <div class="plugin-detail-tabs"><StoreTabs v-model="detailPage" :items="detailPages" :label="t('plugins.details')" /></div>
      <div class="plugin-detail-scroll">
        <Transition name="plugin-detail" mode="out-in" appear>
          <div :key="current.plugin + detailPage">
            <template v-if="detailPage === 'overview'">
              <p class="plugin-description" :class="{ 'is-empty': !current.description }">{{ current.description || t('plugins.noDescription') }}</p>
              <div class="plugin-stat-grid">
                <div><span>{{ t('plugins.categoryField') }}</span><strong>{{ categoryLabel(current.category) }}</strong></div>
                <div><span>{{ t('col.version') }}</span><strong :title="current.version ?? ''">{{ displayVersion(current.version) || '-' }}</strong></div>
                <div><span>{{ t('plugins.instances') }}</span><strong>{{ rows.length }}</strong></div>
                <div v-if="current.author"><span>{{ t('plugins.author') }}</span><strong>{{ current.author }}</strong></div>
              </div>
              <div v-for="row in errors" :key="row.rowId" class="plugin-error" role="alert"><strong>{{ row.build || row.rowId }}</strong><p>{{ row.error }}</p></div>
            </template>
            <ul v-else-if="detailPage === 'builds'" class="plugin-build-list">
              <li v-for="(row, index) in rows" :key="row.rowId" :style="{ '--i': index }">
                <div class="plugin-build-main">
                  <strong>{{ row.build || t('plugins.shared') }}</strong>
                  <span class="mono" :title="row.rowId">{{ row.rowId }}</span>
                </div>
                <NoahTag pill size="sm" :tone="stateTone(row.state)">{{ stateName(row.state, t, te) }}</NoahTag>
                <div class="plugin-build-actions">
                  <button type="button" class="plugin-icon-button" :disabled="row.plugin === SELF_PLUGIN" :title="t('plugins.reload')" :aria-label="t('plugins.reload') + ' ' + row.rowId" @click="$emit('reload', row)"><NoahIcon name="refresh" :size="15" /></button>
                  <button v-if="row.state === 'Active'" type="button" class="plugin-icon-button" :disabled="row.plugin === SELF_PLUGIN" :title="t('plugins.unload')" :aria-label="t('plugins.unload') + ' ' + row.rowId" @click="$emit('unload', row)"><NoahIcon name="pause" :size="15" /></button>
                  <button v-else-if="row.state === 'Unloaded'" type="button" class="plugin-icon-button" :disabled="row.plugin === SELF_PLUGIN" :title="t('plugins.enable')" :aria-label="t('plugins.enable') + ' ' + row.rowId" @click="$emit('load', row)"><NoahIcon name="play" :size="15" /></button>
                </div>
              </li>
            </ul>
            <dl v-else class="plugin-facts">
              <dt>{{ t('plugins.code') }}</dt><dd class="mono">{{ current.plugin }}</dd>
              <dt>{{ t('col.version') }}</dt><dd :title="current.version ?? ''">{{ shortVersion(current.version) }}</dd>
              <template v-if="single"><dt>{{ t('plugins.configurationId') }}</dt><dd class="mono">{{ single.rowId }}</dd></template>
              <dt>{{ t('plugins.configurationSource') }}</dt><dd>{{ sources }}</dd>
            </dl>
          </div>
        </Transition>
      </div>
      <footer v-if="single" class="plugin-detail-actions">
        <p v-if="single.plugin === SELF_PLUGIN" class="text-muted">{{ t('plugins.reload.self') }}</p>
        <div class="plugin-action-buttons">
          <NoahButton class="plugin-action" variant="primary" size="sm" :disabled="single.plugin === SELF_PLUGIN" @click="$emit('reload', single)">{{ t('plugins.reload') }}</NoahButton>
          <NoahButton v-if="single.state === 'Active'" class="plugin-action" variant="secondary" size="sm" :disabled="single.plugin === SELF_PLUGIN" @click="$emit('unload', single)">{{ t('plugins.unload') }}</NoahButton>
          <NoahButton v-if="single.state === 'Active'" class="plugin-action" variant="secondary" size="sm" :disabled="single.plugin === SELF_PLUGIN" @click="$emit('disable', single)">{{ t('plugins.disable') }}</NoahButton>
          <NoahButton v-else-if="single.state === 'Unloaded'" class="plugin-action" variant="secondary" size="sm" :disabled="single.plugin === SELF_PLUGIN" @click="$emit('load', single)">{{ t('plugins.enable') }}</NoahButton>
        </div>
      </footer>
      <footer v-else class="plugin-detail-actions plugin-detail-hint"><NoahIcon name="info" :size="15" /><span>{{ t('plugins.perBuildHint') }}</span></footer>
    </template>
    <p v-else class="plugin-empty text-muted">{{ t('plugins.choose') }}</p>
  </aside>
</template>

<style scoped>
.plugin-detail { position: relative; display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; border: 1px solid var(--border-soft); border-radius: 20px; background: var(--surface); box-shadow: 0 1px 2px rgb(15 23 42 / .04), 0 12px 32px rgb(15 23 42 / .06); }
.plugin-detail-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; }
.plugin-detail-heading { display: flex; align-items: flex-start; gap: 16px; padding: 24px 24px 18px; flex: none; }
.plugin-detail-icon { display: grid; place-items: center; flex: none; width: 52px; height: 52px; border-radius: 16px; color: var(--cat-ink, #475569); background: var(--cat-bg, #eef2f7); box-shadow: inset 0 0 0 1px rgb(255 255 255 / .6); }
.plugin-detail-title { display: grid; gap: 4px; min-width: 0; }
.plugin-detail-title h3 { margin: 0; font-size: 20px; font-weight: 600; letter-spacing: -.01em; line-height: 1.3; overflow-wrap: anywhere; }
.plugin-detail-code { color: var(--text-muted); font-size: 12px; overflow-wrap: anywhere; }
.plugin-detail-tags { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
.plugin-detail-tags .noah-tag { padding: 4px 10px; line-height: 1.2; }
.plugin-store-link { display: inline-flex; align-items: center; gap: 2px; color: var(--text-muted); font-size: 12px; text-decoration: none; }
.plugin-store-link:hover { color: var(--text); }
.plugin-detail-tabs { flex: none; padding: 0 24px; border-bottom: 1px solid var(--border-soft); }
.plugin-description { margin: 22px 24px 0; color: var(--text); font-size: 14px; line-height: 1.75; white-space: pre-wrap; overflow-wrap: anywhere; }
.plugin-description.is-empty { color: var(--text-muted); font-style: italic; }
.plugin-stat-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 20px 24px; }
.plugin-stat-grid > div { display: grid; gap: 4px; padding: 12px 14px; border-radius: 14px; background: var(--hover); }
.plugin-stat-grid span { color: var(--text-muted); font-size: 11.5px; }
.plugin-stat-grid strong { font-size: 14px; font-weight: 600; overflow-wrap: anywhere; }
.plugin-build-list { list-style: none; margin: 0; padding: 14px 16px 18px; display: grid; gap: 8px; }
.plugin-build-list li { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 10px; padding: 10px 10px 10px 14px; border: 1px solid var(--border-soft); border-radius: 14px; background: var(--surface); animation: plugin-rise .3s cubic-bezier(.2,.8,.2,1) backwards; animation-delay: calc(min(var(--i), 14) * 22ms); transition: border-color .15s, box-shadow .15s; }
.plugin-build-list li:hover { border-color: var(--border); box-shadow: 0 4px 14px rgb(15 23 42 / .06); }
.plugin-build-main { display: grid; gap: 2px; min-width: 0; }
.plugin-build-main strong { font-size: 13.5px; font-weight: 600; }
.plugin-build-main span { color: var(--text-muted); font-size: 11.5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.plugin-build-list .noah-tag { padding: 4px 9px; line-height: 1.2; }
.plugin-build-actions { display: flex; gap: 4px; }
.plugin-icon-button { display: grid; place-items: center; width: 32px; height: 32px; padding: 0; border: 0; border-radius: 10px; background: transparent; color: var(--text-muted); cursor: pointer; transition: background .15s, color .15s; }
.plugin-icon-button:hover:not(:disabled) { background: var(--hover); color: var(--text); }
.plugin-icon-button:disabled { opacity: .35; cursor: default; }
.plugin-icon-button:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 1px; }
.plugin-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 14px 20px; padding: 22px 24px; margin: 0; font-size: 13.5px; }
.plugin-facts dt { color: var(--text-muted); }
.plugin-facts dd { margin: 0; text-align: right; overflow-wrap: anywhere; }
.plugin-error { margin: 0 24px 16px; padding: 12px 14px; background: var(--red-soft); color: var(--red); border-radius: 14px; font-size: 13px; }
.plugin-error p { white-space: pre-wrap; overflow-wrap: anywhere; margin: 6px 0 0; }
.plugin-detail-actions { flex: none; border-top: 1px solid var(--border-soft); padding: 16px 24px; }
.plugin-detail-hint { display: flex; align-items: center; gap: 8px; color: var(--text-muted); font-size: 12.5px; }
.plugin-action-buttons { display: grid; grid-template-columns: 1.3fr 1fr 1fr; gap: 8px; }
.plugin-action.noah-btn { width: 100%; min-width: 0; min-height: 40px; padding: 10px 8px; font-size: var(--text-control); }
.plugin-detail-actions p { margin: 0 0 12px; font-size: var(--text-caption); }
.plugin-empty { padding: 36px 20px; text-align: center; }
.plugin-back { display: none; }
.plugin-detail-enter-active, .plugin-detail-leave-active { transition: opacity .16s ease, transform .16s ease; }
.plugin-detail-enter-from, .plugin-detail-leave-to { opacity: 0; transform: translateY(6px); }
.plugin-drawer-close { position: absolute; right: 16px; top: 16px; z-index: 1; display: grid; place-items: center; width: 36px; height: 36px; padding: 0; border: 0; border-radius: 50%; background: transparent; color: var(--text-muted); cursor: pointer; transition: background .15s, transform .2s; }
.plugin-drawer-close:hover { background: var(--hover); color: var(--text); }
.is-drawer.plugin-detail { height: 100%; border: 0; border-radius: 0; box-shadow: none; }
.is-drawer .plugin-detail-heading { padding-right: 64px; }
@keyframes plugin-rise { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
@media (max-width: 760px) { .plugin-back { display: inline-flex; align-self: flex-start; margin: 12px 12px 0; } }
@media (prefers-reduced-motion: reduce) { .plugin-detail-enter-active, .plugin-detail-leave-active, .plugin-build-list li, .plugin-drawer-close { transition: none; animation: none; } }
</style>
