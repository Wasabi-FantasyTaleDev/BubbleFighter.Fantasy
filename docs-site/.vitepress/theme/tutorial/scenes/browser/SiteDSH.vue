<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import Brand from '../../kit/Brand.vue'
import { cues } from '../../cue'
import { dshChinese } from './products'
import type { Code, Step } from '../../types'

// The DeepSeek Harness download page: a deep blue hero whose lines rise in one after another,
// then a dark preview of the app tilts up into place.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const zh = computed(() => dshChinese(props.lang))
const text = computed(() => zh.value
  ? { badge: '预览版', line: '现在，开箱即用', lead: ['无论处理日常工作、编写代码，还是构建自己的 Harness，都可以从这里开始。', 'DeepSeek Harness 面向全球开放测试并同步开源，以可自由组合的插件，拓展 Agent 能力。'], cta: '下载 Windows 版', meet: '认识 DeepSeek Harness', ask: '可以介绍一下自己吗？', thought: '已思考', session: '新会话', plugins: '插件', auto: '自动化任务', ws: '工作区' }
  : { badge: 'PREVIEW', line: 'Ready to use. Right now.', lead: ['Everyday tasks, coding, or your own harness — it starts here.', 'DeepSeek Harness is now in public preview worldwide and open source, with composable plugins that extend what agents can do.'], cta: 'Download for Windows', meet: 'Meet DeepSeek Harness', ask: 'Could you introduce yourself?', thought: 'Thought for a while', session: 'New Session', plugins: 'Plugins', auto: 'Automation', ws: 'Workspace' })
</script>

<template>
  <div class="ds-site">
    <div class="ds-sky" />
    <header class="ds-head">
      <span class="ds-word"><Brand name="dsh" :size="24" tone="#fff" />deepseek<em>HARNESS</em></span>
      <span class="ds-lang"><b :class="{ 'is-on': zh }">中文</b><b :class="{ 'is-on': !zh }">EN</b></span>
    </header>
    <p class="ds-badge"><TpIcon name="sparkle" :size="11" />{{ text.badge }}</p>
    <h1 class="ds-title"><span>DeepSeek Harness</span><span>{{ text.line }}</span></h1>
    <p class="ds-lead"><span v-for="line in text.lead" :key="line">{{ line }}</span></p>
    <div class="ds-cta-row"><span class="ds-cta" data-tp="site-download" :class="{ 'is-pressed': c.between('dlClick', 'dlPanel') }"><TpIcon name="download" :size="17" />{{ text.cta }}<i /><TpIcon name="chevronDown" :size="15" /></span></div>
    <div class="ds-shot">
      <aside><span class="ds-dots"><i /><i /><i /></span><span class="ds-word is-small"><Brand name="dsh" :size="17" tone="#fff" />deepseek<em>HARNESS</em></span><b><TpIcon name="plusCircle" :size="13" />{{ text.session }}</b><span><TpIcon name="puzzle" :size="13" />{{ text.plugins }}</span><span><TpIcon name="clock" :size="13" />{{ text.auto }}</span><small>{{ text.ws }}</small></aside>
      <div class="ds-shot-main"><header>{{ text.meet }}</header><span class="ds-ask">{{ text.ask }}</span><small>{{ text.thought }} ›</small><s style="width: 88%" /><s style="width: 64%" /><s style="width: 30%" /></div>
    </div>
  </div>
</template>
