<script setup>
import { computed, nextTick, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import { useI18n } from '../i18n'
import { animatedCancel, closeDialog, openDialog } from '../composables/dialogMotion'

// Asks what setup does with a database that already holds tables. Keeping is the default; replacing opens a second,
// separate confirmation whose default button goes back, so one stray click or Enter never deletes data.
const props = defineProps({ database: { type: String, required: true } })
const emit = defineEmits(['choose'])
const { t } = useI18n()
const dialog = ref(null)
const confirmDialog = ref(null)
const backButton = ref(null)
const keepButton = ref(null)
const contents = ref(null)
const choice = ref('keep')
const acknowledged = ref(false)
const description = computed(() => contents.value?.accounts == null
  ? t('setup.existingDescriptionTables', { database: props.database, tables: contents.value?.tables ?? 0 })
  : t('setup.existingDescription', { database: props.database, tables: contents.value.tables, accounts: contents.value.accounts }))

async function show(found, current) {
  contents.value = found; choice.value = current === 'replace' ? 'replace' : 'keep'; acknowledged.value = false
  await nextTick()
  openDialog(dialog.value)
  keepButton.value?.focus()
}
async function confirm(value) {
  if (value === 'replace') {
    acknowledged.value = false
    openDialog(confirmDialog.value)
    await nextTick(); backButton.value?.$el?.focus?.()
    return
  }
  emit('choose', 'keep')
  await closeDialog(dialog.value)
}
async function confirmReplace() {
  if (!acknowledged.value) return
  emit('choose', 'replace')
  await closeDialog(confirmDialog.value)
  await closeDialog(dialog.value)
}
function goBack() { return closeDialog(confirmDialog.value) }
// Closing keeps the choice already in effect (keep, unless the operator confirmed a replace earlier).
function cancel(event) { animatedCancel(event) }
function dismiss() { return closeDialog(dialog.value) }
defineExpose({ show })
</script>

<template>
  <Teleport to="body">
    <dialog ref="dialog" class="setup-existing-dialog bff-pop" role="alertdialog" aria-labelledby="setup-existing-title" aria-describedby="setup-existing-description" @cancel="cancel">
      <button type="button" class="bff-pop-close" :aria-label="t('setup.closeDialog')" @click="dismiss"><svg aria-hidden="true" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18" /></svg></button>
      <header style="--i: 0">
        <span class="setup-existing-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><ellipse cx="12" cy="6" rx="7" ry="3" /><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3" /></svg></span>
        <h2 id="setup-existing-title">{{ t('setup.existingTitle') }}</h2>
        <p id="setup-existing-description">{{ description }}</p>
      </header>
      <div class="setup-existing-choices" role="radiogroup" :aria-label="t('setup.existingTitle')" style="--i: 1">
        <label :class="{ 'is-selected': choice === 'keep' }">
          <input ref="keepButton" v-model="choice" type="radio" value="keep" name="existing-data" />
          <span><strong>{{ t('setup.existingKeep') }} <em>{{ t('setup.recommended') }}</em></strong>{{ t('setup.existingKeepDescription') }}</span>
        </label>
        <label class="is-danger" :class="{ 'is-selected': choice === 'replace' }">
          <input v-model="choice" type="radio" value="replace" name="existing-data" />
          <span><strong>{{ t('setup.existingReplace') }}</strong>{{ t('setup.existingReplaceDescription') }}</span>
        </label>
      </div>
      <footer style="--i: 2">
        <NoahButton :variant="choice === 'replace' ? 'danger' : 'primary'" @click="confirm(choice)">
          {{ t(choice === 'replace' ? 'setup.existingReplaceAction' : 'setup.existingKeepAction') }}
        </NoahButton>
      </footer>
    </dialog>
    <dialog ref="confirmDialog" class="setup-existing-dialog setup-existing-danger bff-pop" role="alertdialog" aria-labelledby="setup-replace-title" aria-describedby="setup-replace-description" @cancel="animatedCancel">
      <button type="button" class="bff-pop-close" :aria-label="t('setup.closeDialog')" @click="goBack"><svg aria-hidden="true" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18" /></svg></button>
      <header style="--i: 0">
        <span class="setup-existing-icon is-danger" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4M12 17.5v.01" /></svg></span>
        <h2 id="setup-replace-title">{{ t('setup.replaceTitle') }}</h2>
        <p id="setup-replace-description">{{ t('setup.replaceDescription') }}</p>
      </header>
      <label class="setup-existing-acknowledge" style="--i: 1">
        <input v-model="acknowledged" type="checkbox" />
        <span>{{ t('setup.existingReplaceConfirm', { database }) }}</span>
      </label>
      <footer class="setup-existing-confirm-actions" style="--i: 2">
        <NoahButton ref="backButton" variant="primary" @click="goBack">{{ t('setup.replaceBack') }}</NoahButton>
        <NoahButton variant="danger" :disabled="!acknowledged" @click="confirmReplace">{{ t('setup.existingReplaceAction') }}</NoahButton>
      </footer>
    </dialog>
  </Teleport>
</template>

<style>
.setup-existing-dialog { width: min(560px, calc(100vw - 32px)); max-height: calc(100dvh - 40px); box-sizing: border-box; padding: 28px; color: var(--text-primary, #202124); border: 1px solid #ffffffc0; border-radius: 24px; background: linear-gradient(135deg, #fafcffff, #f1f3f8ff); box-shadow: 0 24px 80px #14233822; font: inherit; overflow: auto; }
.setup-existing-dialog::backdrop { background: #19233738; backdrop-filter: blur(8px); }
.setup-existing-dialog header { text-align: center; }
.setup-existing-icon { display: inline-grid; place-items: center; width: 56px; height: 56px; margin-bottom: 12px; border-radius: 18px; background: linear-gradient(135deg, #fff4e0, #ffe2b8); animation: setup-existing-bob 2.6s ease-in-out .4s infinite; }
.setup-existing-icon svg { width: 30px; height: 30px; fill: none; stroke: #b25e09; stroke-width: 1.6; stroke-linecap: round; }
.setup-existing-dialog h2 { margin: 0 0 8px; font-size: 22px; font-weight: 500; }
.setup-existing-dialog header p { margin: 0; font-size: 13px; line-height: 1.7; color: #4b5563; }
.setup-existing-choices { display: grid; gap: 10px; margin: 20px 0 12px; }
.setup-existing-choices > label { display: flex; gap: 12px; align-items: flex-start; padding: 14px 16px; border: 1px solid #d8dee8; border-radius: 14px; background: #ffffffb8; cursor: pointer; transition: border-color .18s ease, background .18s ease, box-shadow .18s ease, transform .18s ease; }
.setup-existing-choices > label:hover { transform: translateY(-1px); box-shadow: 0 6px 18px #14233812; }
.setup-existing-choices > label.is-selected { border-color: #2f7fd1; background: #eef6ff; box-shadow: 0 0 0 3px #2f7fd122; }
.setup-existing-choices > label.is-danger.is-selected { border-color: #c2410c; background: #fff4ed; box-shadow: 0 0 0 3px #c2410c22; }
.setup-existing-choices input { margin-top: 3px; accent-color: #2f7fd1; }
.setup-existing-choices .is-danger input { accent-color: #c2410c; }
.setup-existing-choices span { display: grid; gap: 4px; font-size: 12.5px; line-height: 1.6; color: #4b5563; }
.setup-existing-choices strong { font-size: 14px; font-weight: 600; color: #1f2937; }
.setup-existing-choices em { margin-left: 6px; padding: 1px 8px; border-radius: 99px; background: #dcfce7; color: #166534; font-size: 11px; font-style: normal; font-weight: 500; }
.setup-existing-acknowledge { display: flex; gap: 10px; align-items: flex-start; padding: 12px 14px; margin-bottom: 12px; border-radius: 12px; background: #fff1f0; color: #9a3412; font-size: 12.5px; line-height: 1.6; cursor: pointer; }
.setup-existing-acknowledge input { margin-top: 3px; accent-color: #c2410c; }
.setup-existing-dialog footer { display: flex; justify-content: flex-end; gap: 10px; }
.setup-existing-danger { width: min(480px, calc(100vw - 32px)); border-color: #fecaca; background: linear-gradient(135deg, #fffafa, #fff1f0); }
.setup-existing-icon.is-danger { background: linear-gradient(135deg, #ffe4e1, #fecaca); animation: setup-existing-throb 1.8s ease-in-out .4s infinite; }
.setup-existing-icon.is-danger svg { stroke: #b42318; }
.setup-existing-danger .setup-existing-acknowledge { margin: 18px 0 16px; }
@keyframes setup-existing-throb { 0%, 100% { transform: none; box-shadow: 0 0 0 0 #ef444400; } 50% { transform: scale(1.05); box-shadow: 0 0 0 8px #ef444418; } }
@keyframes setup-existing-bob { 0%, 100% { transform: none; } 50% { transform: translateY(-3px); } }
@media (prefers-reduced-motion: reduce) { .setup-existing-icon, .setup-existing-choices > label { animation: none; transition: none; } }
</style>
