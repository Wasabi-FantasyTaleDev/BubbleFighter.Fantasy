<script setup lang="ts">
import { computed } from 'vue'
import TpIcon from './TpIcon.vue'
import type { Code } from '../types'

// A macOS desktop in the style of macOS 26 and later: a transparent menu bar with status items, two
// widgets, a column of desktop items and a glass Dock. `app` is the frontmost app named in the menu
// bar; `running` marks Dock items with the running dot; `pressed` presses a Dock item. Wording follows
// the page language the way a Mac set to that language shows it.
const props = withDefaults(defineProps<{ lang?: Code; app?: 'finder' | 'terminal' | 'safari'; running?: string[]; pressed?: string }>(), { lang: 'en', app: 'finder', running: () => [] })

const menus: Record<Code, Record<'finder' | 'terminal' | 'safari', string[]> & { date: string; week: string[]; month: string; docs: string; downloads: string; shots: string; notes: string }> = {
  en: { finder: ['Finder', 'File', 'Edit', 'View', 'Go', 'Window', 'Help'], terminal: ['Terminal', 'Shell', 'Edit', 'View', 'Window', 'Help'], safari: ['Safari', 'File', 'Edit', 'View', 'History', 'Bookmarks', 'Window', 'Help'],
    date: 'Mon Nov 16  21:05', week: ['S', 'M', 'T', 'W', 'T', 'F', 'S'], month: 'NOVEMBER', docs: 'Documents', downloads: 'Downloads', shots: 'Screenshots', notes: 'Server notes' },
  'zh-CN': { finder: ['访达', '文件', '编辑', '显示', '前往', '窗口', '帮助'], terminal: ['终端', 'Shell', '编辑', '显示', '窗口', '帮助'], safari: ['Safari 浏览器', '文件', '编辑', '显示', '历史记录', '书签', '窗口', '帮助'],
    date: '11月16日 周一  21:05', week: ['日', '一', '二', '三', '四', '五', '六'], month: '十一月', docs: '文稿', downloads: '下载', shots: '截图', notes: '服务器笔记' },
  'zh-TW': { finder: ['Finder', '檔案', '編輯', '顯示方式', '前往', '視窗', '輔助說明'], terminal: ['終端機', 'Shell', '編輯', '顯示方式', '視窗', '輔助說明'], safari: ['Safari', '檔案', '編輯', '顯示方式', '瀏覽記錄', '書籤', '視窗', '輔助說明'],
    date: '11月16日 週一  21:05', week: ['日', '一', '二', '三', '四', '五', '六'], month: '十一月', docs: '文件', downloads: '下載項目', shots: '截圖', notes: '伺服器筆記' },
  ko: { finder: ['Finder', '파일', '편집', '보기', '이동', '윈도우', '도움말'], terminal: ['터미널', '셸', '편집', '보기', '윈도우', '도움말'], safari: ['Safari', '파일', '편집', '보기', '방문 기록', '책갈피', '윈도우', '도움말'],
    date: '11월 16일 (월) 21:05', week: ['일', '월', '화', '수', '목', '금', '토'], month: '11월', docs: '문서', downloads: '다운로드', shots: '스크린샷', notes: '서버 메모' },
}
const m = computed(() => menus[props.lang])
// November 2026 starts on a Sunday.
const days = Array.from({ length: 30 }, (_, i) => i + 1)
const dock = [
  { id: 'finder', icon: 'desktop' }, { id: 'launchpad', icon: 'grid' }, { id: 'safari', icon: 'globe' }, { id: 'mail', icon: 'bell' },
  { id: 'photos', icon: 'picture' }, { id: 'music', icon: 'music' }, { id: 'notes', icon: 'book' }, { id: 'calendar', icon: 'clock' },
  { id: 'code', icon: 'code' }, { id: 'terminal', icon: 'terminal' }, { id: 'settings', icon: 'toggle' },
]
</script>

<template>
  <div class="mac-desk">
    <!-- Wallpaper artwork by pixiv@seamonsterping (assets/mac-wallpaper.jpg, not in the public source). -->
    <div class="mac-wall" />

    <div class="mac-menubar">
      <svg class="mac-apple" viewBox="0 0 14 17" width="13" height="16" aria-hidden="true"><path d="M11.6 9c0-2 1.6-2.9 1.7-3-1-1.4-2.4-1.6-2.9-1.6-1.2-.1-2.4.7-3 .7s-1.6-.7-2.6-.7C3.4 4.5 2 5.3 1.3 6.6c-1.5 2.6-.4 6.4 1 8.5.7 1 1.5 2.1 2.6 2.1 1 0 1.4-.7 2.7-.7s1.6.7 2.7.6c1.1 0 1.8-1 2.5-2 .8-1.1 1.1-2.2 1.1-2.3 0 0-2.3-.9-2.3-3.8zM9.6 3c.6-.7 1-1.7.9-2.7-.9 0-1.9.6-2.5 1.3-.6.6-1 1.6-.9 2.6 1 .1 2-.5 2.5-1.2z" fill="currentColor" /></svg>
      <b>{{ m[app][0] }}</b><span v-for="item in m[app].slice(1)" :key="item">{{ item }}</span>
      <span class="mac-status">
        <svg viewBox="0 0 26 13" width="25" height="12" aria-hidden="true"><rect x=".5" y=".5" width="22" height="12" rx="3.5" fill="none" stroke="currentColor" opacity=".55" /><rect x="2" y="2" width="16" height="9" rx="2" fill="currentColor" /><path d="M24 4.5v4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" opacity=".55" /></svg>
        <svg viewBox="0 0 18 13" width="17" height="12" aria-hidden="true"><path d="M9 12.3 6.9 10a3 3 0 0 1 4.2 0zM4.7 7.8a6.2 6.2 0 0 1 8.6 0l-1.1 1.1a4.7 4.7 0 0 0-6.4 0zM2.4 5.5a9.5 9.5 0 0 1 13.2 0l-1.1 1.1a8 8 0 0 0-11 0zM.2 3.2a12.7 12.7 0 0 1 17.6 0l-1.1 1.1a11.2 11.2 0 0 0-15.4 0z" fill="currentColor" /></svg>
        <TpIcon name="search" :size="14" />
        <svg viewBox="0 0 16 14" width="15" height="13" aria-hidden="true"><rect x="1" y="1" width="14" height="5" rx="2.5" fill="none" stroke="currentColor" stroke-width="1.3" /><circle cx="11.5" cy="3.5" r="1.4" fill="currentColor" /><rect x="1" y="8" width="14" height="5" rx="2.5" fill="none" stroke="currentColor" stroke-width="1.3" /><circle cx="4.5" cy="10.5" r="1.4" fill="currentColor" /></svg>
        <span>{{ m.date }}</span>
      </span>
    </div>

    <div class="mac-widgets">
      <div class="mac-widget is-clock"><b>21:05</b><small>{{ m.date.replace('21:05', '').trim() }}</small></div>
      <div class="mac-widget is-calendar">
        <small>{{ m.month }}</small>
        <div class="mac-cal"><span v-for="(d, n) in m.week" :key="'w' + n" class="is-head">{{ d }}</span><span v-for="d in days" :key="d" :class="{ 'is-today': d === 16 }">{{ d }}</span></div>
      </div>
    </div>

    <div class="mac-icons">
      <span><i class="mac-folder" />{{ m.docs }}</span>
      <span><i class="mac-folder" />{{ m.downloads }}</span>
      <span><i class="mac-folder" />{{ m.shots }}</span>
      <span><i class="mac-file" />{{ m.notes }}</span>
    </div>

    <slot />

    <div class="mac-dock">
      <template v-for="item in dock" :key="item.id">
        <span v-if="item.id === 'terminal'" class="mac-dock-sep" />
        <span :data-tp="`dock-${item.id}`" :class="['mac-app', `is-${item.id}`, { 'is-running': running.includes(item.id), 'is-pressed': pressed === item.id }]"><TpIcon :name="item.icon" :size="24" /></span>
      </template>
      <span class="mac-dock-sep" />
      <span class="mac-app is-trash"><TpIcon name="trash" :size="24" /></span>
    </div>
  </div>
</template>
