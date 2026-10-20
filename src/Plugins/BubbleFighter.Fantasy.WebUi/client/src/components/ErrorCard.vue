<script setup>
import { computed } from 'vue'
import NoahAlert from '../noah/components/ui/NoahAlert.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import { useI18n } from '../i18n'

const props = defineProps({
  /** The ApiError (or any Error) to explain. */
  error: { type: Error, required: true }
})
defineEmits(['retry'])

const { t } = useI18n()
// A missing optional service answers 404 or 503; say so plainly instead of echoing the server's wording.
const message = computed(() => {
  if (props.error.status === 404 || props.error.status === 503) return t('common.unavailable')
  if (props.error.status === 0) return t('conn.offline')
  return props.error.message
})
</script>

<template>
  <NoahAlert tone="error" :show-icon="false">
    <div class="error-card">
      <span>{{ message }}</span>
      <NoahButton size="xs" @click="$emit('retry')">{{ t('common.retry') }}</NoahButton>
    </div>
  </NoahAlert>
</template>
