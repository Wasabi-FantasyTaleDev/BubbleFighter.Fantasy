<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import AppMark from '../../kit/AppMark.vue'
import Robot from '../../kit/Robot.vue'
import { cues } from '../../cue'
import type { Code, Step } from '../../types'

const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
// Title sequence of the product site: the "o" becomes a pill that cycles icons, settles back into
// a letter, then the Chinese tagline rises in and the line re-centres.
const sinceSite = computed(() => props.t - c.value.time('site'))
const titleIcon = computed(() => ['loop', 'toggle', 'briefcase', 'codeTag'][Math.min(3, Math.max(0, Math.floor((sinceSite.value - 300) / 430)))])
const titleSettled = computed(() => sinceSite.value >= 2050)
const taglineIn = computed(() => sinceSite.value >= 2350)
</script>

<template>
  <div class="bs-site">
    <div class="bs-site-glow" />
    <header class="bs-site-head">
      <AppMark :size="34" full />
      <nav><b>首页</b><span>Buddy One <em>Hot</em></span><span>定价</span><span>企业</span><span>文档</span><span>案例</span><span>合作</span><span>活动</span></nav>
      <span class="bs-site-dl">下载 <TpIcon name="download" :size="14" /></span><span>登录</span>
    </header>
    <h1 class="bs-site-title">
      <span>W</span><span class="bs-o" :class="{ 'is-settled': titleSettled }"><span class="bs-o-pill"><Transition name="bs-icon"><TpIcon :key="titleIcon" :name="titleIcon" :size="34" :stroke="2.2" /></Transition></span><span class="bs-o-letter">o</span></span><span>rkBuddy</span><span class="bs-tagline" :class="{ 'is-in': taglineIn }">我帮你</span>
    </h1>
    <p class="bs-site-lead">WorkBuddy 是全场景 AI 办公工作台。说出要求、开始执行任务、交付完整成果，你的办公好搭子。</p>
    <div class="bs-site-actions">
      <span class="bs-site-cta" data-tp="site-download" :class="{ 'is-pressed': c.between('dlClick', 'dlPanel') }">立即下载 WorkBuddy</span>
      <span class="bs-site-online">在线使用 WorkBuddy</span>
    </div>
    <div class="bs-site-shot">
      <div class="bs-shot-side"><i /><i /><i /><b>WorkBuddy</b><span v-for="item in ['新建任务', '助理', '项目', '专家·技能·连接器', '自动化', '资料库', '更多']" :key="item">{{ item }}</span></div>
      <div class="bs-shot-main"><b>WorkBuddy, 我帮你</b><div class="bs-shot-seg"><span>日常办公</span><span>代码开发</span><span>创意设计</span></div><div class="bs-shot-box">今天帮你做些什么？</div><Robot class="bs-shot-robot" :size="54" /></div>
    </div>
  </div>
</template>
