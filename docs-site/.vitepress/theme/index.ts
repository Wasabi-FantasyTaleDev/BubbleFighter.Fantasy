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
  },
} satisfies Theme
