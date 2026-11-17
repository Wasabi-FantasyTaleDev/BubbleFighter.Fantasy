<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TpSwitch from './kit/TpSwitch.vue'
import TutorialPlayer from './TutorialPlayer.vue'
import InstallCommands from './InstallCommands.vue'
import { tutorialData } from './registry'
import type { Code, Platform, Tutorial } from './types'
import './tutorial.css'

const props = defineProps<{ slug: string; lang: string }>()
const code = computed(() => (['en', 'zh-CN', 'zh-TW', 'ko'].includes(props.lang) ? props.lang : 'zh-CN') as Code)
const data = shallowRef<Tutorial>()
// Some tutorials depend on services that are hard to reach for some readers; those readers confirm
// a notice first, and the player (with its autoplay) only appears afterwards.
const accepted = ref(false)
const okButton = ref<HTMLElement>()
const notice = computed(() => (accepted.value ? undefined : data.value?.notice?.[code.value]))
watch(notice, value => { if (value) nextTick(() => okButton.value?.focus({ focusVisible: false } as FocusOptions)) })
// A tutorial drawn for several systems shows a switch beside the page title; Linux is the default.
const { t: label } = useI18n()
const platform = ref<Platform>('linux')
const platforms = computed(() => [{ id: 'linux' as Platform, icon: 'terminal', label: 'Linux' }, { id: 'windows' as Platform, icon: 'windows', label: label('tutorial.commands.windows') }])
// A tutorial with install commands switches between the animation and the commands.
type View = 'tutorial' | 'commands'
const view = ref<View>('tutorial')
const views = computed(() => [{ id: 'tutorial' as View, icon: 'play', label: label('tutorial.commands.tutorialView'), fill: true }, { id: 'commands' as View, icon: 'code', label: label('tutorial.commands.commandsView') }])
const credit = computed(() => data.value?.credits?.[platform.value]?.[code.value] ?? '')
const shown = computed(() => (data.value?.platforms ? { ...data.value, steps: data.value.platforms[platform.value] } : data.value))
watch(() => props.slug, async slug => {
  data.value = undefined
  // A link ending in #windows (the home page's Windows tab) opens the Windows version.
  platform.value = typeof location !== 'undefined' && location.hash === '#windows' ? 'windows' : 'linux'
  view.value = 'tutorial'
  accepted.value = false
  const load = tutorialData[slug]
  if (!load) throw new Error(`Unknown tutorial: ${slug}`)
  data.value = (await load()).default
}, { immediate: true })
</script>

<template>
  <article class="tutorial-page">
    <Teleport v-if="data?.platforms" defer to="#tutorial-heading-platform">
      <TpSwitch v-model="platform" :options="platforms" :label="label('tutorial.commands.system')" />
    </Teleport>
    <Teleport v-if="data?.commands" defer to="#tutorial-heading-tools">
      <TpSwitch v-model="view" :options="views" :label="label('tutorial.commands.view')" />
    </Teleport>
    <!-- Switching the system or the view fades the old content out and the new one in. -->
    <Transition name="tp-view" mode="out-in">
      <InstallCommands v-if="data?.commands && view === 'commands'" :key="`commands-${platform}`" :lang="code" :platform="platform" />
      <TutorialPlayer v-else-if="shown && !notice" :key="`player-${platform}`" :tutorial="shown" :lang="code" :credit="credit" />
      <div v-else key="loading" class="tp-loading" aria-hidden="true" />
    </Transition>
    <Teleport to="body">
      <Transition name="tp-notice">
        <div v-if="notice" class="tp-notice-mask" @keydown.esc="accepted = true">
          <div class="tp-notice" role="alertdialog" aria-modal="true" aria-labelledby="tp-notice-title">
            <h2 id="tp-notice-title">{{ notice.title }}</h2>
            <p v-for="line in notice.body" :key="line">{{ line }}</p>
            <button ref="okButton" type="button" class="tp-notice-ok" @click="accepted = true">{{ notice.ok }}</button>
          </div>
        </div>
      </Transition>
    </Teleport>
  </article>
</template>
