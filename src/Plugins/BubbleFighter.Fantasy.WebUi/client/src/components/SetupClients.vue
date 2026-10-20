<script setup>
import { computed, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import ClientDataPicker from './ClientDataPicker.vue'
import { useI18n } from '../i18n'
import { useUiStore } from '../stores/ui'
import { clientName } from './clientNames'

const props = defineProps({ modelValue: { type: Array, required: true }, profiles: { type: Array, required: true } })
const emit = defineEmits(['update:modelValue', 'busy'])
const importing = ref(new Set())
const experimentalPending = ref([])
const experimentalSelectAll = ref(false)
function importingChanged(profile, value) {
  if (value) importing.value.add(profile)
  else importing.value.delete(profile)
  emit('busy', importing.value.size > 0)
}
const { t } = useI18n()
const ui = useUiStore()
const name = id => clientName(props.profiles, id, ui.lang)
function isPreparing(profile) {
  return profile?.setupEnabled === false || profile?.activationEnabled === false
}
function isExperimental(profile) { return profile?.experimental === true }
const choices = computed(() => props.profiles.filter(p => p.id !== 'management')
  .sort((left, right) => Number(isPreparing(left)) - Number(isPreparing(right))))
const selectedCount = computed(() => choices.value.filter(p => !isPreparing(p) && props.modelValue.some(c => c.profile === p.id)).length)
const availableChoices = computed(() => choices.value.filter(p => !isPreparing(p)))
const allSelected = computed(() => availableChoices.value.length > 0 && selectedCount.value === availableChoices.value.length)
const partiallySelected = computed(() => selectedCount.value > 0 && !allSelected.value)
function toggleAll(selected) {
  if (importing.value.size > 0) return
  const available = choices.value.filter(p => !isPreparing(p))
  const experimental = available.filter(p => isExperimental(p) && !props.modelValue.some(c => c.profile === p.id))
  if (selected && experimental.length) { experimentalPending.value = experimental; experimentalSelectAll.value = true; return }
  emit('update:modelValue', selected ? available.map(p => props.modelValue.find(c => c.profile === p.id)
    || { profile: p.id, clientDataPath: '', deferClientDataImport: true }) : [])
}
function toggle(profile, selected) {
  const candidate = props.profiles.find(p => p.id === profile)
  if (isPreparing(candidate)) return
  if (selected && isExperimental(candidate)) { experimentalPending.value = [candidate]; experimentalSelectAll.value = false; return }
  emit('update:modelValue', selected ? [...props.modelValue, { profile, clientDataPath: '', deferClientDataImport: true }]
    : props.modelValue.filter(c => c.profile !== profile))
}
function confirmExperimental() {
  const pending = experimentalSelectAll.value ? availableChoices.value : experimentalPending.value
  experimentalPending.value = []
  experimentalSelectAll.value = false
  if (!pending.length) return
  emit('update:modelValue', [...props.modelValue, ...pending
    .filter(profile => !props.modelValue.some(client => client.profile === profile.id))
    .map(profile => ({ profile: profile.id, clientDataPath: '', deferClientDataImport: true }))])
}
function cancelExperimental() { experimentalPending.value = []; experimentalSelectAll.value = false }
function updatePath(profile, path) {
  emit('update:modelValue', props.modelValue.map(c => c.profile === profile ? {
    ...c, clientDataPath: path, deferClientDataImport: !path.trim()
  } : c))
}
</script>

<template>
  <fieldset class="setup-client-versions">
    <legend>
      <span>{{ t('setup.profile') }}</span>
      <label class="setup-client-choice setup-client-select-all" :class="{ 'is-selected': allSelected, 'is-partial': partiallySelected }">
        <input type="checkbox" :disabled="importing.size > 0 || !choices.length" :checked="allSelected" :indeterminate="partiallySelected" @change="toggleAll($event.target.checked)" />
        <span class="setup-client-check" aria-hidden="true"><svg viewBox="0 0 16 16"><path v-if="partiallySelected" d="M3 8h10" /><path v-else d="m3 8 3 3 7-7" /></svg></span>
        <span>{{ t('setup.selectAllClients') }}</span>
      </label>
    </legend>
    <p class="setup-hint">{{ t('setup.multipleClients') }}</p>
    <div class="setup-client-checkboxes">
      <label v-for="profile in choices" :key="profile.id" class="setup-client-choice" :class="{ 'is-selected': modelValue.some(c => c.profile === profile.id), 'is-preparing': isPreparing(profile), 'is-experimental': isExperimental(profile) }" :aria-disabled="isPreparing(profile) || undefined">
        <input type="checkbox" :disabled="importing.size > 0 || isPreparing(profile)" :checked="modelValue.some(c => c.profile === profile.id)" @change="toggle(profile.id, $event.target.checked)" />
        <span class="setup-client-check" aria-hidden="true"><svg viewBox="0 0 16 16"><path d="m3 8 3 3 7-7" /></svg></span>
        <span>{{ name(profile.id) }}<small v-if="isPreparing(profile)"> {{ t('setup.preparing') }}</small><small v-else-if="isExperimental(profile)"> {{ t('setup.experimental') }}</small></span>
      </label>
    </div>
  </fieldset>
  <TransitionGroup name="setup-client-row" tag="div" class="setup-client-paths">
    <section v-for="client in modelValue" :key="client.profile" class="setup-client-path">
      <div class="setup-client-path-inner">
      <h3>{{ name(client.profile) }}</h3>
      <div class="setup-field">
        <p class="setup-hint">{{ t('setup.preparedDataRoot') }}</p>
        <ClientDataPicker :profile="client.profile" :model-value="client.clientDataPath" @update:model-value="updatePath(client.profile, $event)" @busy="importingChanged(client.profile, $event)" />
      </div>
      </div>
    </section>
  </TransitionGroup>
  <Teleport to="body">
    <div v-if="experimentalPending.length" class="setup-experimental-modal" role="presentation" @click.self="cancelExperimental">
      <section class="setup-experimental-dialog" role="dialog" aria-modal="true" :aria-label="t('setup.experimentalTitle')">
        <div class="setup-experimental-heading">
          <span class="setup-experimental-mark" aria-hidden="true">!</span>
          <h3>{{ t('setup.experimentalTitle') }}</h3>
        </div>
        <p>{{ t('setup.experimentalConfirm') }}</p>
        <div class="setup-experimental-actions">
          <NoahButton variant="glass" size="lg" class="setup-experimental-cancel" @click="cancelExperimental">{{ t('setup.experimentalCancel') }}</NoahButton>
          <NoahButton variant="primary" size="lg" class="setup-experimental-confirm" @click="confirmExperimental">{{ t('setup.experimentalContinue') }}</NoahButton>
        </div>
      </section>
    </div>
  </Teleport>
</template>

<style>
.setup-client-versions { min-width: 0; border: 0; padding: 0; margin: 0; }
.setup-client-versions legend { display: flex; align-items: center; justify-content: space-between; gap: 16px; width: 100%; padding: 0; font-size: 13px; font-weight: 500; margin-bottom: 6px; }
.setup-client-checkboxes { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 185px), 1fr)); gap: 8px; margin-top: 12px; }
.setup-client-choice { position: relative; display: flex; align-items: center; gap: 9px; padding: 11px 12px; border: 1px solid #18253214; border-radius: 10px; font-size: 13px; cursor: pointer; background: #ffffff70; transition: background .25s ease, border-color .25s ease, transform .2s ease; }
.setup-client-choice.is-selected, .setup-client-choice.is-partial { border-color: #161a2266; background: #fff; }
.setup-client-choice:active { transform: scale(.97); }
.setup-client-choice:has(input:focus-visible) { outline: 2px solid #123a56; outline-offset: 3px; }
.setup-client-choice:has(input:disabled) { cursor: default; opacity: .6; }
.setup-client-choice.is-preparing { color: #7b858f; background: #eef1f3; border-color: #d9dee3; filter: grayscale(.55); }
.setup-client-choice.is-preparing .setup-client-check { border-color: #c4cbd1; background: #e1e5e8; }
.setup-client-choice.is-preparing small { display: inline-block; color: #8b949c; font-size: 11px; }
.setup-client-choice.is-experimental:not(.is-preparing) small { display: inline-block; color: #737b84; font-size: 11px; }
.setup-experimental-modal { position: fixed; inset: 0; z-index: 10000; width: 100vw; height: 100dvh; display: grid; place-items: center; padding: 24px; background: rgba(12, 27, 40, .48); -webkit-backdrop-filter: blur(12px) saturate(.9); backdrop-filter: blur(12px) saturate(.9); }
.setup-experimental-dialog { width: min(520px, calc(100vw - 40px)); padding: 30px 32px 26px; border: 1px solid rgba(255, 255, 255, .72); border-radius: 28px; background: linear-gradient(145deg, rgba(255, 255, 255, .96), rgba(241, 249, 253, .9)); color: #18364b; box-shadow: 0 28px 80px rgba(8, 32, 49, .3), inset 0 1px rgba(255, 255, 255, .9); }
.setup-experimental-heading { display: flex; align-items: center; gap: 12px; }
.setup-experimental-mark { display: grid; place-items: center; width: 28px; height: 28px; flex: 0 0 28px; border-radius: 50%; color: #fff; background: #15191d; font-size: 16px; font-weight: 700; }
.setup-experimental-dialog h3 { margin: 0; font-size: 20px; font-weight: 600; letter-spacing: -.02em; }
.setup-experimental-dialog p { margin: 18px 0 0; color: #486173; line-height: 1.75; font-size: 15px; }
.setup-experimental-actions { display: flex; justify-content: flex-end; align-items: center; gap: 10px; margin-top: 28px; }
.setup-experimental-actions .noah-btn { min-width: 108px; }
.setup-experimental-actions .setup-experimental-cancel { background: rgba(255, 255, 255, .46); border-color: rgba(82, 122, 145, .28); color: #486173; box-shadow: inset 0 1px rgba(255, 255, 255, .9); }
.setup-experimental-actions .setup-experimental-cancel:hover:not(:disabled) { background: rgba(255, 255, 255, .78); border-color: rgba(61, 105, 132, .48); }
.setup-experimental-actions .setup-experimental-confirm { background: #15191d; border-color: #15191d; color: #fff; box-shadow: 0 8px 18px rgba(21, 25, 29, .2); }
.setup-experimental-actions .setup-experimental-confirm:hover:not(:disabled) { background: #2b3137; border-color: #2b3137; box-shadow: 0 10px 24px rgba(21, 25, 29, .28); }
@media (max-width: 520px) { .setup-experimental-modal { padding: 16px; } .setup-experimental-dialog { padding: 24px 20px 20px; border-radius: 22px; } .setup-experimental-actions { flex-direction: column-reverse; align-items: stretch; } .setup-experimental-actions .noah-btn { width: 100%; } }
.setup-client-choice input { position: absolute; opacity: 0; width: 1px; height: 1px; }
.setup-client-choice.setup-client-select-all { flex-shrink: 0; padding: 3px 0 3px 12px; border: 0; background: transparent; }
.setup-client-check { flex: 0 0 16px; width: 16px; height: 16px; box-sizing: border-box; border: 1px solid #9da5b0; border-radius: 5px; transition: background .22s ease, border-color .22s ease, transform .3s cubic-bezier(.2, 1.5, .4, 1); transform: scale(.94); }
.setup-client-check svg { display: block; width: 14px; height: 14px; fill: none; stroke: #fff; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
.setup-client-check path { stroke-dasharray: 18; stroke-dashoffset: 18; transition: stroke-dashoffset .24s ease; }
.is-selected .setup-client-check, .is-partial .setup-client-check { background: #171a1e; border-color: #171a1e; transform: scale(1); }
.is-selected .setup-client-check path, .is-partial .setup-client-check path { stroke-dashoffset: 0; }
.setup-client-paths { display: flex; flex-direction: column; padding: 2px 3px; }
.setup-client-path { display: grid; grid-template-rows: 1fr; opacity: 1; }
.setup-client-path-inner { min-height: 0; overflow: hidden; }
.setup-client-path-inner > h3 { border-top: 1px solid #18253212; padding-top: 15px; }
.setup-client-path-inner > .setup-field { margin: 12px 0 18px; }
.setup-client-row-enter-active, .setup-client-row-leave-active { transition: grid-template-rows .32s cubic-bezier(.2,.8,.2,1), opacity .24s ease, transform .32s ease; }
.setup-client-row-enter-from, .setup-client-row-leave-to { grid-template-rows: 0fr; opacity: 0; transform: translateY(-6px); }
.setup-client-row-leave-active { pointer-events: none; }
.setup-client-path h3 { margin: 0; font-size: 13px; font-weight: 500; }
.setup-client-path-row { display: flex; gap: 10px; align-items: center; }
.setup-client-path-row > :first-child { flex: 1; min-width: 0; }
@media (max-width: 480px) { .setup-client-path-row { flex-direction: column; align-items: stretch; } }
@media (prefers-reduced-motion: reduce) { .setup-client-choice, .setup-client-choice *, .setup-client-path { transition: none !important; } }
</style>
