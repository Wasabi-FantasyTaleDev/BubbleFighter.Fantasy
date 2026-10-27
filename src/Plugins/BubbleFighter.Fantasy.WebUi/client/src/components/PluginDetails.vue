<script setup>
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import StoreTabs from './StoreTabs.vue'
import { useI18n } from '../i18n'

const props = defineProps({ current: Object, drawer: Boolean })
defineEmits(['reload', 'unload', 'disable', 'load', 'back', 'close'])
const { t, te } = useI18n()
const SELF_PLUGIN = 'admin.http'
const detailPage = ref('definition')
const hasDescription = computed(() => !!props.current?.description?.trim())
const storeTarget = computed(() => props.current?.storeEntry?.id
  ? { path: '/store', query: { plugin: props.current.storeEntry.id } } : null)
const updateVersion = computed(() => props.current?.availableVersion?.trim())
const dependencyGroups = computed(() => [
  { id: 'platform', label: t('plugins.platformDependencies'), items: [
    ...(props.current?.dependencies?.launcherVersion ? [{ id: 'launcher', name: 'Fantasy Tale Launcher', version: props.current.dependencies.launcherVersion }] : []),
    ...(props.current?.dependencies?.nirinsouLinkVersion ? [{ id: 'nirinsou-link', name: 'Nirinsou Link', version: props.current.dependencies.nirinsouLinkVersion }] : [])
  ] },
  { id: 'server', label: t('plugins.serverDependencies'), items: props.current?.dependencies?.server || [] },
  { id: 'client', label: t('plugins.clientDependencies'), items: props.current?.dependencies?.client || [] }
].filter(group => group.items.length))
const hasDependencies = computed(() => dependencyGroups.value.length > 0)
const detailPages = computed(() => [
  ...(hasDescription.value ? [{ id: 'details', label: t('plugins.details') }] : []),
  { id: 'definition', label: t('plugins.definition') },
  ...(hasDependencies.value ? [{ id: 'dependencies', label: t('plugins.dependencies') }] : [])
])
const authorText = computed(() => {
  const authors = props.current?.authors
  return (Array.isArray(authors) ? authors.join(', ') : props.current?.author) || ''
})
watch([() => props.current?.rowId, hasDescription], () => {
  detailPage.value = hasDescription.value ? 'details' : 'definition'
}, { immediate: true })
watch(hasDependencies, value => {
  if (!value && detailPage.value === 'dependencies') detailPage.value = hasDescription.value ? 'details' : 'definition'
})
const stateName = state => te('state.' + state) ? t('state.' + state) : state
const stateTone = state => state === 'Active' ? 'success'
  : ['Failed', 'CleanupFailed', 'WorkFailed'].includes(state) ? 'danger'
    : ['Pending', 'Unloading', 'CleanupIncomplete'].includes(state) ? 'warning' : 'default'
function shortVersion(version) {
  if (!version) return '-'
  const plus = version.indexOf('+')
  return plus < 0 ? version : version.slice(0, plus + 1) + version.slice(plus + 1, plus + 8)
}
function displayVersion(version) {
  return version?.split('+', 1)[0] || ''
}
</script>

<template>
      <aside class="plugin-detail" :class="{ 'is-drawer': drawer }" :aria-label="t('plugins.details')">
        <template v-if="current">
          <button v-if="drawer" ref="closeButton" type="button" class="plugin-drawer-close" :aria-label="t('common.close')" @click="$emit('close')"><NoahIcon name="close" :size="18" /></button>
          <NoahButton v-if="!drawer" class="plugin-back" variant="glass" size="sm" icon="chevron-left" @click="$emit('back')">{{ t('plugins.back') }}</NoahButton>
                <header class="plugin-detail-heading">
                  <div class="plugin-detail-title"><h3>{{ current.name || current.plugin }}</h3><NoahTag pill size="sm" :tone="stateTone(current.state)">{{ stateName(current.state) }}</NoahTag><NoahTag v-if="updateVersion" pill size="sm" tone="info">{{ t('plugins.updateAvailable', { from: displayVersion(current.version), to: displayVersion(updateVersion) }) }}</NoahTag></div>
                  <div class="plugin-detail-links"><span class="plugin-detail-code mono">{{ current.plugin }}</span><RouterLink v-if="storeTarget" class="plugin-store-link" :to="storeTarget">{{ t('plugins.openStore') }}<NoahIcon name="chevron-right" :size="14" /></RouterLink></div>
                </header>
                <div class="plugin-detail-tabs"><StoreTabs v-model="detailPage" :items="detailPages" :label="t('plugins.details')" /></div>
          <div class="plugin-detail-scroll">
            <Transition name="plugin-detail" mode="out-in" appear>
              <div :key="current.rowId + detailPage">
                <template v-if="detailPage === 'definition'">
                <dl class="plugin-facts">
                  <template v-if="current.name && current.name !== current.plugin"><dt>{{ t('plugins.name') }}</dt><dd>{{ current.name }}</dd></template>
                  <dt>{{ t('plugins.code') }}</dt><dd>{{ current.plugin }}</dd>
                  <dt>{{ t('col.version') }}</dt><dd :title="current.version ?? ''">{{ shortVersion(current.version) }}</dd>
                  <dt>{{ t('plugins.configurationId') }}</dt><dd>{{ current.rowId }}</dd>
                  <dt>{{ t('plugins.configurationSource') }}</dt><dd>{{ current.source }}</dd>
                </dl>
                <div v-if="current.error" class="plugin-error" role="alert"><strong>{{ t('col.error') }}</strong><p>{{ current.error }}</p></div>
                </template>
                <template v-else-if="detailPage === 'dependencies'">
                  <div class="plugin-dependencies">
                    <section v-for="group in dependencyGroups" :key="group.id" class="plugin-dependency-group">
                      <h4>{{ group.label }}</h4>
                      <ul>
                        <li v-for="dependency in group.items" :key="dependency.id">
                          <div><strong>{{ dependency.name || dependency.id }}</strong><span v-if="dependency.name && group.id !== 'platform'" class="mono">{{ dependency.id }}</span></div>
                          <span v-if="dependency.version" class="plugin-dependency-version">{{ dependency.version }}</span>
                        </li>
                      </ul>
                    </section>
                  </div>
                </template>
                <template v-else>
                  <dl class="plugin-facts">
                    <template v-if="authorText"><dt>{{ t('plugins.author') }}</dt><dd>{{ authorText }}</dd></template>
                    <template v-if="displayVersion(current.version)"><dt>{{ t('col.version') }}</dt><dd>{{ displayVersion(current.version) }}</dd></template>
                  </dl>
                  <section class="plugin-description"><h4>{{ t('plugins.description') }}</h4><p>{{ current.description }}</p></section>
                </template>
              </div>
            </Transition>
          </div>
          <footer class="plugin-detail-actions">
            <p v-if="current.plugin === SELF_PLUGIN" class="text-muted">{{ t('plugins.reload.self') }}</p>
            <div class="plugin-action-buttons">
              <NoahButton class="plugin-action" variant="primary" size="sm" :disabled="current.plugin === SELF_PLUGIN" @click="$emit('reload', current)">{{ t('plugins.reload') }}</NoahButton>
              <NoahButton v-if="current.state === 'Active'" class="plugin-action" variant="secondary" size="sm" :disabled="current.plugin === SELF_PLUGIN" @click="$emit('unload', current)">{{ t('plugins.unload') }}</NoahButton>
              <NoahButton v-if="current.state === 'Active'" class="plugin-action" variant="secondary" size="sm" :disabled="current.plugin === SELF_PLUGIN" @click="$emit('disable', current)">{{ t('plugins.disable') }}</NoahButton>
              <NoahButton v-else-if="current.state === 'Unloaded'" class="plugin-action" variant="secondary" size="sm" :disabled="current.plugin === SELF_PLUGIN" @click="$emit('load', current)">{{ t('plugins.enable') }}</NoahButton>
            </div>
          </footer>
        </template>
        <p v-else class="plugin-empty text-muted">{{ t('plugins.choose') }}</p>
      </aside>
</template>

<style scoped>
.plugin-detail { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
.plugin-detail-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; }
.plugin-detail-heading { display: flex; flex-direction: column; align-items: flex-start; gap: 10px; padding: 20px; border-bottom: 1px solid var(--border-soft); text-align: left; }
.plugin-detail-heading, .plugin-detail-tabs { flex: none; }
.plugin-detail-tabs { padding: 8px 20px 0; border-bottom: 1px solid var(--border-soft); }
.plugin-detail-heading h3 { margin: 0; font-size: var(--text-title); line-height: 1.4; overflow-wrap: anywhere; max-width: 100%; }
.plugin-detail-title { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 12px; min-width: 0; max-width: 100%; }
.plugin-detail-title > .noah-tag { flex: none; padding: 5px 9px; line-height: 1.2; }
.plugin-detail-code { color: var(--text-muted); font-size: var(--text-caption); overflow-wrap: anywhere; max-width: 100%; }
.plugin-detail-links { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: 8px 16px; width: 100%; }
.plugin-store-link { display: inline-flex; align-items: center; gap: 4px; color: var(--text-muted); font-size: var(--text-caption); text-decoration: none; }
.plugin-store-link:hover { color: var(--text); }
.plugin-store-link:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 3px; border-radius: var(--radius-xs); }
.plugin-description { padding: 0 20px 24px; }
.plugin-description h4 { font-size: var(--text-control); font-weight: 500; margin: 0 0 10px; }
.plugin-description p { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; color: var(--text-muted); font-size: var(--text-control); line-height: 1.7; }
.plugin-dependencies { padding: 24px 20px; display: grid; gap: 24px; }
.plugin-dependency-group h4 { margin: 0 0 12px; font-size: var(--text-control); font-weight: 500; }
.plugin-dependency-group ul { list-style: none; padding: 0; margin: 0; border: 1px solid var(--border-soft); border-radius: var(--radius-sm); overflow: hidden; }
.plugin-dependency-group li { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 14px; font-size: var(--text-control); }
.plugin-dependency-group li + li { border-top: 1px solid var(--border-soft); }
.plugin-dependency-group li > div { display: grid; gap: 6px; min-width: 0; overflow-wrap: anywhere; }
.plugin-dependency-group strong { font-weight: 500; }
.plugin-dependency-group .mono, .plugin-dependency-version { color: var(--text-muted); font-size: var(--text-caption); }
.plugin-dependency-version { flex: none; }
.plugin-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 16px 20px; padding: 24px 20px; margin: 0; font-size: var(--text-control); }
.plugin-facts dt { color: var(--text-muted); }
.plugin-facts dd { margin: 0; text-align: right; overflow-wrap: anywhere; }
.plugin-error { margin: 0 20px 20px; padding: 14px; background: var(--red-soft); color: var(--red); border-radius: var(--radius-sm); font-size: var(--text-control); }
.plugin-error p { white-space: pre-wrap; overflow-wrap: anywhere; margin: 8px 0 0; }
.plugin-detail-actions { flex: none; border-top: 1px solid var(--border-soft); padding: 16px 20px; }
.plugin-action-buttons { display: grid; grid-template-columns: 1.3fr 1fr 1fr; gap: 8px; }
.plugin-action.noah-btn { width: 100%; min-width: 0; min-height: 40px; padding: 10px 8px; font-size: var(--text-control); }
.plugin-detail-actions p { margin: 0 0 12px; font-size: var(--text-caption); }
.plugin-empty { padding: 36px 20px; text-align: center; }
.plugin-back { display: none; }
.plugin-detail-enter-active, .plugin-detail-leave-active { transition: opacity .12s ease, transform .12s ease; }
.plugin-detail-enter-from, .plugin-detail-leave-to { opacity: 0; transform: translateY(4px); }
.plugin-detail { position: relative; }
.plugin-drawer-close { position: absolute; right: 16px; top: 16px; display: grid; place-items: center; padding: 8px; border: 0; border-radius: var(--radius-xs); background: transparent; color: var(--text-muted); cursor: pointer; }
.plugin-drawer-close:hover { background: var(--hover); }
.is-drawer.plugin-detail { display: flex; height: 100%; border: 0; border-radius: 0; background: var(--surface); }
.is-drawer .plugin-detail-heading { padding-right: 60px; }
@media (max-width: 760px) { .plugin-back { display: inline-flex; align-self: flex-start; margin: 12px 12px 0; } }
@media (prefers-reduced-motion: reduce) { .plugin-detail-enter-active, .plugin-detail-leave-active { transition: none; } }
</style>
