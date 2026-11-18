import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme'
import Layout from './Layout.vue'
import { createDocsI18n } from './locales'
import '@fontsource-variable/geist'
import './noah/styles/tokens.css'
import './noah/styles/glass.css'
import './style.css'

export default {
  extends: DefaultTheme,
  Layout,
  enhanceApp({ app }) {
    app.use(createDocsI18n())
    // Every publish renames the script chunks, so a tab opened before it asks for files that no longer exist and
    // a language switch, search or tutorial silently fails. Reload once to fetch the current site instead.
    if (typeof window !== 'undefined') {
      window.addEventListener('vite:preloadError', event => {
        const key = 'docs-reloaded-for-stale-chunk'
        try {
          if (sessionStorage.getItem(key) === location.pathname) return
          sessionStorage.setItem(key, location.pathname)
        } catch {}
        event.preventDefault()
        location.reload()
      })
    }
  },
} satisfies Theme
