<script setup lang="ts">
import { computed, type PropType } from 'vue'
import Desktop from '../kit/Desktop.vue'
import TpIcon from '../kit/TpIcon.vue'
import Brand from '../kit/Brand.vue'
import { osText } from '../kit/os'
import { cues, easeInOut } from '../cue'
import { products } from './browser/products'
import BaiduPages from './browser/BaiduPages.vue'
import GooglePages from './browser/GooglePages.vue'
import SiteWorkBuddy from './browser/SiteWorkBuddy.vue'
import SiteDSH from './browser/SiteDSH.vue'
import SiteGPT from './browser/SiteGPT.vue'
import StoreWindow from './browser/StoreWindow.vue'
import type { Code, Step } from '../types'
import './browser/browser.css'

// Runtime declaration: the docs site compiles without TypeScript, which imported prop types need.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const os = computed(() => osText(props.lang))

// Simplified Chinese readers search on Baidu, except for ChatGPT: its Baidu results are crowded with
// ads that bury the official site. Everyone else searches on Google.
const product = computed(() => products[(props.step.props?.product as keyof typeof products) ?? 'workbuddy'])
const engine = computed(() => (props.lang === 'zh-CN' && !product.value.googleOnly ? 'baidu' : 'google'))
const query = computed(() => product.value.query)
const page = computed(() => (on('site') ? 'site' : on('results') ? 'results' : 'home'))
const searchUrl = computed(() => engine.value === 'baidu'
  ? (page.value === 'results' ? `https://www.baidu.com/s?wd=${encodeURIComponent(query.value)}` : 'https://www.baidu.com/')
  : (page.value === 'results' ? `https://www.google.com/search?q=${query.value.replace(/ /g, '+')}` : 'https://www.google.com/'))
const url = computed(() => (page.value === 'site' ? product.value.url(props.lang) : searchUrl.value))
const searchWord = computed(() => ({ en: 'Search', 'zh-TW': '搜尋', ko: '검색', 'zh-CN': '搜索' })[props.lang])
const searchTitle = computed(() => engine.value === 'baidu'
  ? (on('results') ? `${query.value}_百度搜索` : '百度一下，你就知道')
  : (on('results') ? `${query.value} - Google ${searchWord.value}` : 'Google'))
// Baidu opens the result in a new tab; Google navigates the same one.
const sameTabSite = computed(() => !product.value.newTab && on('site'))

const download = computed(() => {
  const span = c.value.time('done') - c.value.time('keep')
  const p = easeInOut(c.value.progress('keep', span))
  const total = product.value.totalMB
  const got = total * p
  return { p, text: os.value.progress(got.toFixed(1), total, Math.max(1, Math.round((total - got) / 9.4))) }
})
const installP = computed(() => c.value.progress('install', c.value.time('installed') - c.value.time('install')))
const apps = computed(() => [on('open') ? 'browser' : '', on('explorer') ? 'explorer' : '', on('store') ? 'store' : ''].filter(Boolean))
const agentReady = computed(() => on('installed') || on('getDone'))
</script>

<template>
  <Desktop :apps="apps" :show-agent="agentReady" :brand="product.id" :lang="lang">
    <!-- Browser -->
    <div class="tp-win bs-browser" :class="{ 'is-open': on('open'), 'is-behind': on('explorer') || on('store') }">
      <div class="bs-tabs">
        <span class="bs-tabs-menu"><TpIcon name="chevronDown" :size="14" /></span>
        <span v-if="product.newTab" class="bs-tab"><TpIcon name="grid" :size="13" />{{ os.settingsTab }}<TpIcon name="close" :size="12" /></span>
        <span class="bs-tab" :class="{ 'is-active': !product.newTab || !on('site') }">
          <Transition name="tp-swap" mode="out-in">
            <span :key="sameTabSite ? 'site' : 'search'" class="bs-tab-label">
              <Brand v-if="sameTabSite" :name="product.id" :size="14" :tone="product.id === 'dsh' ? '#4d6bfe' : '#111'" /><span v-else class="bs-favicon" :class="engine === 'google' ? 'is-google' : 'is-search'" />
              {{ sameTabSite ? product.siteTitle(lang) : searchTitle }}
            </span>
          </Transition>
          <TpIcon name="close" :size="12" />
        </span>
        <span v-if="product.newTab" class="bs-tab is-new" :class="{ 'is-shown': on('linkClick'), 'is-active': on('site') }"><Brand :name="product.id" :size="14" />{{ product.siteTitle(lang) }}<TpIcon name="close" :size="12" /></span>
        <span class="bs-tabs-plus"><TpIcon name="plus" :size="15" /></span>
        <span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span>
      </div>
      <div class="bs-toolbar">
        <TpIcon name="back" :size="17" /><TpIcon name="refresh" :size="16" />
        <div class="bs-address"><TpIcon name="lock" :size="13" /><Transition name="tp-swap" mode="out-in"><span :key="url">{{ url }}</span></Transition></div>
        <span class="bs-tool"><TpIcon name="star" :size="16" /></span><span class="bs-tool"><TpIcon name="puzzle" :size="16" /></span>
        <span v-if="!product.store" class="bs-dl-icon" :class="{ 'is-shown': on('dlClick'), 'is-active': on('dlPanel') && !on('explorer') }">
          <svg v-if="on('keep') && !on('done')" class="bs-dl-ring" viewBox="0 0 36 36"><circle cx="18" cy="18" r="15" :style="{ strokeDashoffset: 94.2 * (1 - download.p) }" /></svg>
          <TpIcon name="download" :size="16" />
        </span>
        <span class="bs-tool"><span class="bs-avatar" /></span><span class="bs-tool"><TpIcon name="more" :size="18" :stroke="3" /></span>
      </div>

      <div class="bs-page">
        <BaiduPages v-if="engine === 'baidu'" :t="t" :step="step" :lang="lang" :product="product.id" :query="query" />
        <GooglePages v-else :t="t" :step="step" :lang="lang" :product="product.id" :query="query" />
        <template v-if="on('site')">
          <SiteWorkBuddy v-if="product.id === 'workbuddy'" :t="t" :step="step" :lang="lang" />
          <SiteDSH v-else-if="product.id === 'dsh'" :t="t" :step="step" :lang="lang" />
          <SiteGPT v-else :t="t" :step="step" :lang="lang" />
        </template>
      </div>

      <!-- Downloads flyout -->
      <div v-if="!product.store" class="bs-downloads" :class="{ 'is-open': on('dlPanel') && !on('explorer') }">
        <header><b>{{ os.downloads }}</b><span><TpIcon name="folder" :size="16" /><TpIcon name="search" :size="16" /><TpIcon name="more" :size="16" :stroke="3" /></span></header>
        <div class="bs-dl-item" :class="{ 'is-warning': product.warn && !on('keep'), 'is-done': on('done') }">
          <span class="bs-dl-file"><TpIcon :name="product.warn && !on('keep') ? 'shield' : 'file'" :size="18" /></span>
          <div class="bs-dl-body">
            <template v-if="product.warn && !on('keep')">
              <p>{{ os.keepWarn(product.file) }}</p>
              <div class="bs-dl-buttons"><span data-tp="dl-keep" :class="{ 'is-pressed': c.between('keepPress', 'keep') }">{{ os.keep }}</span><span>{{ os.remove }}</span></div>
            </template>
            <template v-else>
              <p class="bs-dl-name">{{ product.file }}</p>
              <template v-if="!on('done')"><small>{{ download.text }}</small><span class="bs-dl-bar"><i :style="{ transform: `scaleX(${download.p})` }" /></span></template>
              <small v-else class="bs-dl-open">{{ os.openFile }}</small>
            </template>
          </div>
          <span v-if="on('done')" class="bs-dl-actions"><span data-tp="dl-folder"><TpIcon name="folder" :size="17" /></span><span><TpIcon name="trash" :size="17" /></span></span>
        </div>
        <footer>{{ os.seeMore }}</footer>
      </div>

      <!-- "Open Microsoft Store?" prompt the browser shows before handing over -->
      <div v-if="product.store" class="bs-ext" :class="{ 'is-open': c.between('extPrompt', 'store') }">
        <b>{{ os.extTitle('Microsoft Store') }}</b>
        <p>{{ os.extBody('https://chatgpt.com') }}</p>
        <footer><span class="is-primary" data-tp="ext-open" :class="{ 'is-pressed': c.between('extPress', 'store') }">{{ os.extOpen('Microsoft Store') }}</span><span>{{ os.cancel }}</span></footer>
      </div>
    </div>

    <!-- Explorer -->
    <div v-if="on('explorer')" class="tp-win bs-explorer is-open">
      <div class="ex-tabs"><span class="ex-tab"><TpIcon name="folder" :size="15" />{{ os.downloads }}<TpIcon name="close" :size="12" /></span><TpIcon name="plus" :size="15" /><span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span></div>
      <div class="ex-commands"><span><TpIcon name="plusCircle" :size="16" />{{ os.create }}</span><TpIcon name="cut" :size="16" /><TpIcon name="copy" :size="16" /><TpIcon name="paste" :size="16" /><TpIcon name="trash" :size="16" /><span><TpIcon name="sort" :size="16" />{{ os.sort }}</span><span><TpIcon name="view" :size="16" />{{ os.view }}</span><TpIcon name="more" :size="16" :stroke="3" /></div>
      <div class="ex-address"><TpIcon name="back" :size="15" /><TpIcon name="forward" :size="15" /><TpIcon name="up" :size="15" /><div class="ex-path"><TpIcon name="folder" :size="14" />{{ os.thisPc }} › {{ os.downloads }}</div><div class="ex-search">{{ os.searchIn(os.downloads) }}<TpIcon name="search" :size="14" /></div></div>
      <div class="ex-body">
        <ul class="ex-nav"><li><TpIcon name="home" :size="15" />{{ os.home }}</li><li><TpIcon name="desktop" :size="15" />{{ os.desktop }}</li><li class="is-current"><TpIcon name="download" :size="15" />{{ os.downloads }}</li><li><TpIcon name="file" :size="15" />{{ os.documents }}</li><li><TpIcon name="picture" :size="15" />{{ os.pictures }}</li><li><TpIcon name="music" :size="15" />{{ os.music }}</li><li><TpIcon name="video" :size="15" />{{ os.videos }}</li><li class="ex-gap"><TpIcon name="pc" :size="15" />{{ os.thisPc }}</li><li><TpIcon name="network" :size="15" />{{ os.network }}</li></ul>
        <div class="ex-list">
          <div class="ex-head"><span>{{ os.name }}</span><span>{{ os.modified }}</span><span>{{ os.type }}</span><span>{{ os.size }}</span></div>
          <div class="ex-group">⌄ {{ os.today }}</div>
          <div class="ex-row" data-tp="exp-file" :class="{ 'is-hover': on('tooltip'), 'is-selected': on('run') }"><span><Brand :name="product.id" :size="16" :tone="product.id === 'dsh' ? '#4d6bfe' : '#111'" />{{ product.file }}</span><span>{{ os.stamp }}</span><span>{{ os.app }}</span><span>{{ product.kb }}</span></div>
          <div class="ex-tooltip" :class="{ 'is-shown': c.between('tooltip', 'run') }">{{ product.file }}<br>{{ os.fileDesc }}: {{ product.description }}<template v-if="product.version"><br>{{ os.fileVersion }}: {{ product.version }}</template><br>{{ os.size }}: {{ product.totalMB }} MB</div>
        </div>
      </div>
      <div class="ex-status">{{ os.items(1) }}</div>
    </div>

    <!-- Installer -->
    <div v-if="on('install')" class="tp-win bs-installer is-open">
      <div class="bs-installer-head"><Brand :name="product.id" :size="18" :tone="product.id === 'dsh' ? '#4d6bfe' : '#111'" />{{ os.setup(product.app) }}<TpIcon name="close" :size="14" /></div>
      <div class="bs-installer-body">
        <Brand :name="product.id" :size="56" :tone="product.id === 'dsh' ? '#4d6bfe' : '#111'" />
        <b>{{ on('installed') ? os.installed : os.installing(product.app) }}</b>
        <span class="bs-dl-bar is-wide"><i :style="{ transform: `scaleX(${installP})` }" /></span>
        <small>{{ on('installed') ? os.installedHint(product.app) : `${Math.round(installP * 100)}%` }}</small>
      </div>
    </div>

    <!-- Microsoft Store -->
    <StoreWindow v-if="product.store && on('store')" :t="t" :step="step" :lang="lang" />
  </Desktop>
</template>
