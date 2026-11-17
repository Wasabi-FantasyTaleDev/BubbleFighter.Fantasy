<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import Brand from '../../kit/Brand.vue'
import { cues } from '../../cue'
import { products, dshChinese } from './products'
import type { Code, Step } from '../../types'

// Google's home and result pages in the reader's language. Results are reduced to the one the
// tutorial opens; the rest of the page is drawn as neutral placeholders.
const props = defineProps({
  t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true },
  product: { type: String, required: true }, query: { type: String, required: true },
})
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const typed = computed(() => c.value.typed(props.query, 'typeStart', 'typeEnd'))

const words = {
  en: { about: 'About', store: 'Store', images: 'Images', signIn: 'Sign in', mode: 'AI Mode', chips: ['Create images', 'Ask about files', 'Brainstorm', 'I’m Feeling Lucky'], tabs: ['All', 'Images', 'Videos', 'News', 'Shopping', 'Forums', 'More'], left: ['Advertising', 'Business', 'How Search works'], right: ['Privacy', 'Terms', 'Settings'], lead: 'Build, create, and do more with AI tools from Google' },
  'zh-CN': { about: '关于 Google', store: 'Google 商店', images: '图片', signIn: '登录', mode: 'AI 模式', chips: ['创建图片', '询问文件相关问题', '头脑风暴', '手气不错'], tabs: ['全部', '图片', '视频', '新闻', '购物', '论坛', '更多'], left: ['广告', '商务', 'Google 搜索的运作方式'], right: ['隐私权', '条款', '设置'], lead: '借助 Google 的 AI 工具构建、创作并完成更多工作' },
  'zh-TW': { about: '關於 Google', store: 'Google 商店', images: '圖片', signIn: '登入', mode: 'AI 模式', chips: ['建立圖片', '詢問檔案相關問題', '集思廣益', '好手氣'], tabs: ['全部', '圖片', '影片', '新聞', '購物', '論壇', '更多'], left: ['廣告', '商業', '搜尋服務的運作方式'], right: ['隱私權', '條款', '設定'], lead: '使用 Google 的 AI 工具建構、創作並完成更多工作' },
  ko: { about: 'Google 정보', store: '스토어', images: '이미지', signIn: '로그인', mode: 'AI 모드', chips: ['이미지 만들기', '파일에 관해 질문하기', '브레인스토밍', 'I’m Feeling Lucky'], tabs: ['전체', '이미지', '동영상', '뉴스', '쇼핑', '포럼', '더보기'], left: ['광고', '비즈니스', '검색의 원리'], right: ['개인정보처리방침', '약관', '설정'], lead: 'Google의 AI 도구로 더 많이 만들고 해내세요' },
}
const w = computed(() => words[props.lang as keyof typeof words] ?? words.en)
const suggestions = computed(() => props.product === 'dsh'
  ? [' desktop', ' download', ' plugins', ' windows', ' install', ' cli', ' tutorial']
  : [' login', ' download', ' app', ' for windows', ' codex', ' free', ' desktop'])
const lower = computed(() => props.query.toLowerCase())
const dshZh = computed(() => dshChinese(props.lang))
const sitelinks = computed(() => props.product === 'dsh'
  ? (dshZh.value ? [['Harness 用户协议', '欢迎使用 DeepSeek Harness！'], ['Harness 隐私政策', 'DeepSeek Harness 收集的信息分为两类…']] : [['Harness Terms', 'Welcome to DeepSeek Harness! …'], ['Harness Privacy Policy', 'DeepSeek Harness collects two categories of …']])
  : [['Free', 'Try ChatGPT Free today and use AI to write, learn, plan, and get …'], ['Download', 'Chat on the go, have voice conversations, create detailed …'], ['Log in or sign up', 'Log in or sign up to pick up where you left off …'], ['Overview', 'ChatGPT helps you learn, create, and get things done. Ask …']])
</script>

<template>
  <!-- Search home -->
  <div class="bs-home gg-home" :class="{ 'is-gone': on('results') }">
    <nav class="gg-top"><span>{{ w.about }}</span><span>{{ w.store }}</span><i /><span>Gmail</span><span>{{ w.images }}</span><TpIcon name="grid" :size="18" /><b>{{ w.signIn }}</b></nav>
    <div class="gg-logo"><span>G</span><span>o</span><span>o</span><span>g</span><span>l</span><span>e</span></div>
    <div class="gg-box" data-tp="search-box" :class="{ 'is-focus': on('boxClick'), 'is-open': on('suggest') && !on('search') }">
      <div class="gg-box-row">
        <TpIcon name="plus" :size="20" />
        <span class="gg-query">{{ typed }}<i v-if="on('boxClick') && !on('search')" class="tp-caret" /></span>
        <template v-if="!typed"><TpIcon name="mic" :size="19" /><TpIcon name="view" :size="19" /><span class="gg-mode"><TpIcon name="search" :size="15" />{{ w.mode }}</span></template>
        <template v-else><TpIcon name="sparkle" :size="18" /><span class="gg-go" data-tp="search-go" :class="{ 'is-pressed': c.between('search', 'results') }"><TpIcon name="forward" :size="17" :stroke="2.2" /></span></template>
      </div>
      <ul class="gg-suggest">
        <li v-for="tail in suggestions" :key="tail"><TpIcon name="search" :size="15" /><span>{{ lower }}<b>{{ tail }}</b></span></li>
      </ul>
    </div>
    <div class="gg-chips"><span v-for="chip in w.chips" :key="chip">{{ chip }}</span></div>
    <footer class="gg-foot"><span v-for="item in w.left" :key="item">{{ item }}</span><em>{{ w.lead }}</em><span v-for="item in w.right" :key="item">{{ item }}</span></footer>
  </div>

  <!-- Results -->
  <div v-if="on('results')" class="bs-results gg-results" :class="{ 'is-gone': on('site') }">
    <div class="gg-head">
      <div class="gg-logo is-small"><span>G</span><span>o</span><span>o</span><span>g</span><span>l</span><span>e</span></div>
      <div class="gg-box is-small"><div class="gg-box-row"><span class="gg-query">{{ query }}</span><TpIcon name="close" :size="18" /><TpIcon name="mic" :size="18" /><TpIcon name="view" :size="18" /></div></div>
      <span class="gg-head-right"><TpIcon name="share" :size="18" /><TpIcon name="grid" :size="18" /><b>{{ w.signIn }}</b></span>
    </div>
    <nav class="gg-tabs"><span>{{ w.mode }}</span><b>{{ w.tabs[0] }}</b><span v-for="tab in w.tabs.slice(1)" :key="tab">{{ tab }}</span></nav>
    <div class="gg-columns">
      <div class="gg-main">
        <div class="gg-result">
          <div class="gg-site"><span class="gg-fav"><Brand :name="product === 'dsh' ? 'dsh' : 'chatgpt'" :size="18" :tone="product === 'dsh' ? '#4d6bfe' : '#111'" /></span><div><b>{{ product === 'dsh' ? 'DeepSeek' : 'ChatGPT' }}</b><small>{{ product === 'dsh' ? 'https://www.deepseek.com › harness' : 'https://chatgpt.com' }}</small></div><TpIcon name="more" :size="16" :stroke="3" /></div>
          <a :data-tp="product === 'dsh' ? 'result-link' : undefined" :class="{ 'is-visited': product === 'dsh' && on('linkClick') }">{{ product === 'dsh' ? products.dsh.siteTitle(lang) : 'ChatGPT: Chat, Work, Create & Code with AI' }}</a>
          <p v-if="product === 'dsh'">{{ dshZh ? '使用 DeepSeek Harness 处理文档、分析表格、编写代码和安排任务，并通过可组合的插件扩展它的能力。' : 'Use DeepSeek Harness to work with documents, analyze spreadsheets, write code, and schedule tasks. Extend its capabilities with composable plugins.' }}</p>
          <p v-else>Use ChatGPT to answer questions, write, create images, complete work, and code — all in one place. Get started for free or download the app.</p>
          <div class="gg-links">
            <div v-for="([title, text], n) in sitelinks" :key="title" class="gg-link">
              <div><span :data-tp="product === 'chatgpt' && n === 1 ? 'result-link' : undefined" :class="{ 'is-visited': product === 'chatgpt' && n === 1 && on('linkClick') }">{{ title }}</span><small>{{ text }}</small></div><TpIcon name="chevronRight" :size="16" />
            </div>
          </div>
        </div>
        <div v-for="n in 2" :key="n" class="gg-filler"><i /><div><s style="width: 160px" /><s style="width: 420px" /><s style="width: 560px" /><s style="width: 500px" /></div></div>
      </div>
      <aside v-if="product === 'chatgpt'" class="gg-panel">
        <b>ChatGPT</b>
        <div class="gg-panel-art"><span class="is-a" /><span class="is-b"><Brand name="chatgpt" :size="70" tone="#111" /></span><span class="is-c" /><span class="is-d" /></div>
        <s style="width: 330px" /><s style="width: 300px" /><s style="width: 340px" /><s style="width: 200px" />
      </aside>
    </div>
  </div>
</template>
