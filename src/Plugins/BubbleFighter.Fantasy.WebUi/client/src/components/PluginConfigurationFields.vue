<script setup>
import NoahInput from '../noah/components/ui/NoahInput.vue'

defineProps({ modelValue: { type: Object, required: true }, disabled: Boolean })
const emit = defineEmits(['update:modelValue'])
function change(values, key, value) { emit('update:modelValue', { ...values, [key]: value }) }
</script>

<template>
  <div class="plugin-config-fields">
    <template v-for="(value, key) in modelValue" :key="key">
      <fieldset v-if="value !== null && typeof value === 'object' && !Array.isArray(value)">
        <legend class="mono">{{ key }}</legend>
        <PluginConfigurationFields :model-value="value" :disabled="disabled" @update:model-value="change(modelValue, key, $event)" />
      </fieldset>
      <label v-else-if="typeof value === 'boolean'" class="plugin-config-toggle">
        <span class="mono">{{ key }}</span>
        <input type="checkbox" :checked="value" :disabled="disabled" @change="change(modelValue, key, $event.target.checked)" />
      </label>
      <label v-else-if="typeof value === 'string' || typeof value === 'number'">
        <span class="mono">{{ key }}</span>
        <NoahInput :type="typeof value === 'number' ? 'number' : /password|secret|token|api_key/i.test(key) ? 'password' : 'text'"
          :model-value="value" :disabled="disabled" :step="typeof value === 'number' ? 'any' : undefined"
          @update:model-value="change(modelValue, key, typeof value === 'number' ? ($event === '' ? null : Number($event)) : $event)" />
      </label>
      <div v-else class="plugin-config-complex"><span class="mono">{{ key }}</span><code>{{ JSON.stringify(value) }}</code></div>
    </template>
  </div>
</template>

<style scoped>
.plugin-config-fields { display: grid; gap: 16px; }
label { display: grid; gap: 8px; min-width: 0; }
fieldset { margin: 0; padding: 14px; border: 1px solid var(--border); border-radius: 12px; min-width: 0; }
legend { padding: 0 6px; font-size: var(--text-caption); }
.plugin-config-toggle { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.plugin-config-toggle input { accent-color: var(--text-primary); width: 18px; height: 18px; }
.plugin-config-complex { display: grid; gap: 8px; overflow-wrap: anywhere; }
</style>
