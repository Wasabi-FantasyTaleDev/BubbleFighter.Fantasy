import { copyFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { defineConfig } from 'vitepress'

const locales = {
  root: { label: '中文（简体）', lang: 'zh-CN', title: 'Bubble Fighter Fantasy' },
  'zh-tw': { label: '中文（繁体）', lang: 'zh-TW', title: 'Bubble Fighter Fantasy' },
  ko: { label: '한국어', lang: 'ko', title: 'Bubble Fighter Fantasy' },
  en: { label: 'English', lang: 'en', title: 'Bubble Fighter Fantasy' },
}

export default defineConfig({
  title: 'Bubble Fighter Fantasy',
  description: 'Documentation for the Bubble Fighter Fantasy community server.',
  // Only this curated directory is published; repository research is never a content input.
  srcDir: 'content',
  base: process.env.DOCS_BASE || '/',
  cleanUrls: true,
  lastUpdated: false,
  appearance: true,
  locales,
  head: [['link', { rel: 'icon', type: 'image/x-icon', href: `${process.env.DOCS_BASE || '/'}favicon.ico?v=20261015` }]],
  themeConfig: {
    search: {
      provider: 'local',
      options: {
        locales: {
          root: { translations: { button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' }, modal: { noResultsText: '没有找到相关内容', resetButtonTitle: '清空搜索', footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' } } } },
          'zh-tw': { translations: { button: { buttonText: '搜尋文件', buttonAriaLabel: '搜尋文件' }, modal: { noResultsText: '找不到相關內容', resetButtonTitle: '清除搜尋', footer: { selectText: '選取', navigateText: '切換', closeText: '關閉' } } } },
          ko: { translations: { button: { buttonText: '문서 검색', buttonAriaLabel: '문서 검색' }, modal: { noResultsText: '검색 결과가 없습니다', resetButtonTitle: '검색 지우기', footer: { selectText: '선택', navigateText: '이동', closeText: '닫기' } } } },
        },
      },
    },
  },
  markdown: { theme: { light: 'github-light', dark: 'github-dark' } },
  // The site serves the installers at <base>install.sh and <base>install.ps1; deploy/ stays their only source.
  buildEnd(site) {
    for (const name of ['install.sh', 'install.ps1']) {
      copyFileSync(fileURLToPath(new URL(`../../deploy/${name}`, import.meta.url)), join(site.outDir, name))
    }
  },
})
