<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import AppMark from '../../kit/AppMark.vue'
import Robot from '../../kit/Robot.vue'
import { cues } from '../../cue'
import type { Code, Step } from '../../types'

// Baidu's home and result pages, drawn for Simplified Chinese readers.
const props = defineProps({
  t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true },
  product: { type: String, required: true }, query: { type: String, required: true },
})
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const typed = computed(() => c.value.typed(props.query, 'typeStart', 'typeEnd'))
const suggestions = computed(() => ({
  dsh: ['在哪下载', '官网', '怎么用', '下载', '教程', '和插件'],
} as Record<string, string[]>)[props.product] ?? ['怎么读', '是什么', ' 官网', '如何使用', ' 下载', '网页版'])
const related = ['在哪下载', '免费下载官网', '桌面版', '网页版', '插件怎么装', '怎么用'].map(item => `deepseek${item}`)
</script>

<template>
  <!-- Search home -->
  <div class="bs-home" :class="{ 'is-gone': on('results') }">
    <nav class="bs-home-nav"><span v-for="item in ['新闻', 'hao123', '地图', '贴吧', '视频', '图片', '网盘', '文库', '更多']" :key="item">{{ item }}</span></nav>
    <div class="bs-home-user"><span>设置</span><b>登录</b></div>
    <div class="bs-logo"><span class="bs-logo-mark"><TpIcon name="search" :size="26" :stroke="2.4" /></span>百度</div>
    <div class="bs-box" data-tp="search-box" :class="{ 'is-focus': on('boxClick') }">
      <span v-if="!typed" class="bs-placeholder">搜索或输入网址</span>
      <span class="bs-query">{{ typed }}<i v-if="on('boxClick') && !on('search')" class="tp-caret" /></span>
      <span class="bs-box-tools"><TpIcon name="mic" :size="18" /><TpIcon name="plusCircle" :size="18" /></span>
      <span class="bs-go" data-tp="search-go" :class="{ 'is-pressed': c.between('search', 'results') }">百度一下</span>
    </div>
    <ul class="bs-suggest" :class="{ 'is-open': on('suggest') && !on('search') }">
      <li v-for="tail in suggestions" :key="tail"><TpIcon name="search" :size="13" /><span><em>{{ typed }}</em>{{ (query + tail).slice(typed.length) }}</span></li>
    </ul>
    <div class="bs-hot">
      <b>百度热搜</b>
      <span v-for="n in 8" :key="n" class="bs-hot-row"><i>{{ n }}</i><s :style="{ width: `${150 + ((n * 47) % 120)}px` }" /></span>
    </div>
  </div>

  <!-- Results -->
  <div v-if="on('results')" class="bs-results" :class="{ 'is-gone': on('site') }">
    <div class="bs-results-top">
      <div class="bs-logo is-small"><span class="bs-logo-mark"><TpIcon name="search" :size="16" :stroke="2.4" /></span>百度</div>
      <div class="bs-box is-small"><span class="bs-query">{{ query }}</span><span class="bs-go">百度一下</span></div>
    </div>
    <nav class="bs-results-tabs"><b>综合</b><span v-for="item in ['文心', '图片', '资讯', '视频', '笔记', '地图', '贴吧', '文库', '更多']" :key="item">{{ item }}</span></nav>
    <template v-if="product === 'workbuddy'">
      <div class="bs-brand">
        <div class="bs-brand-head"><AppMark :size="40" /><span>腾讯 WorkBuddy</span><em>官方</em></div>
        <strong>AI 专家团</strong><strong>你的职场超能力</strong>
        <div class="bs-brand-art"><Robot :size="150" /></div>
        <div class="bs-brand-bar"><span v-for="item in ['免部署 安装即用', '全领域专家执行', '自定义技能扩展', '7×24h 助理协作']" :key="item">{{ item }}</span></div>
      </div>
      <div class="bs-result">
        <a data-tp="result-link" :class="{ 'is-visited': on('linkClick') }">腾讯<em>WorkBuddy</em> - AI Agent 办公新范式</a>
        <p><em>WorkBuddy</em>，AI 原生的桌面智能体工作台，以自然语言驱动办公自动化。</p>
        <div class="bs-result-app"><AppMark :size="44" /><div><b>WorkBuddy <em>官方版</em></b><span>版本：5.11.3　系统：win/mac　大小：429MB</span></div><span class="bs-result-dl">一键下载</span></div>
      </div>
    </template>
    <div v-else class="bd-columns">
      <div class="bd-main">
        <div class="bs-result">
          <a data-tp="result-link" :class="{ 'is-visited': on('linkClick') }"><em>DeepSeek Harness</em>|共探智能上限</a>
          <p><em>Harness</em> 现在，开箱即用。无论处理日常工作、编写代码，还是构建自己的 <em>Harness</em>，都可以从这里开始。下载 Windows 版…</p>
          <small class="bd-source">深度求索</small>
        </div>
        <div v-for="n in 3" :key="n" class="bd-filler">
          <i class="bd-thumb" />
          <div><s style="width: 340px" /><s style="width: 420px" /><s style="width: 380px" /><s style="width: 120px" /></div>
        </div>
      </div>
      <aside class="bd-side">
        <b>相关搜索</b>
        <span v-for="item in related" :key="item"><TpIcon name="search" :size="13" />{{ item }}</span>
      </aside>
    </div>
  </div>
</template>
