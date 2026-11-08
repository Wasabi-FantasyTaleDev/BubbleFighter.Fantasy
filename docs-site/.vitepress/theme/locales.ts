import { createI18n } from 'vue-i18n'
import zhCN from './locales/zh-CN.json'
import zhTW from './locales/zh-TW.json'
import ko from './locales/ko.json'
import en from './locales/en.json'

export const languages = [
  {
    "key": "root",
    "label": "中文（简体）",
    "prefix": "",
    "code": "zh-CN"
  },
  {
    "key": "zh-tw",
    "label": "中文（繁体）",
    "prefix": "zh-tw/",
    "code": "zh-TW"
  },
  {
    "key": "ko",
    "label": "한국어",
    "prefix": "ko/",
    "code": "ko"
  },
  {
    "key": "en",
    "label": "English",
    "prefix": "en/",
    "code": "en"
  }
] as const

export const routes = [
  "getting-started/installation",
  "getting-started/quickstart",
  "guides/configuration",
  "guides/plugins",
  "guides/sdk-protocol",
  "guides/deployment",
  "guides/launcher",
  "guides/store",
  "guides/self-hosted-store",
  "guides/troubleshooting",
  "project/contributing",
  "project/changelog"
]

export const messages = { 'zh-CN': zhCN, 'zh-TW': zhTW, ko, en }

export const labels = { root: zhCN, 'zh-tw': zhTW, ko, en }

/** Creates an application-local translator for each documentation render. */
export function createDocsI18n(initialLocale = 'zh-CN') {
  return createI18n({ legacy: false, locale: initialLocale, fallbackLocale: 'en', messages })
}

/** Resolves leaf messages before passing them to components as strings. */
export function translatedLabels(t: (key: string) => string) {
  return {
    terminal: {
      clear: t("terminal.clear"),
      restore: t("terminal.restore"),
      minimize: t("terminal.minimize"),
      expand: t("terminal.expand"),
      reduce: t("terminal.reduce"),
      copy: t("terminal.copy"),
      copied: t("terminal.copied"),
      copyFailed: t("terminal.copyFailed"),
    },
    releases: {
      title: t("releases.title"),
      intro: t("releases.intro"),
      latest: t("releases.latest"),
      all: t("releases.all"),
    },
    docs: t("docs"),
    start: t("start"),
    guides: t("guides"),
    project: t("project"),
    home: t("home"),
    pages: [t("pages.0"), t("pages.1"), t("pages.2"), t("pages.3"), t("pages.4"), t("pages.5"), t("pages.6"), t("pages.7"), t("pages.8"), t("pages.9"), t("pages.10"), t("pages.11")],
    outline: t("outline"),
    overview: t("overview"),
    theme: t("theme"),
    languages: t("languages"),
    menu: t("menu"),
    close: t("close"),
    skip: t("skip"),
    previous: t("previous"),
    next: t("next"),
    updated: t("updated"),
    footer: t("footer"),
    implementation: t("implementation"),
    sdk: t("sdk"),
    hero: t("hero"),
    intro: t("intro"),
    action: t("action"),
    secondary: t("secondary"),
    badge: t("badge"),
    cards: [t("cards.0"), t("cards.1"), t("cards.2")],
    descriptions: [t("descriptions.0"), t("descriptions.1"), t("descriptions.2")],
    download: {
      launcherTitle: t("download.launcherTitle"),
      launcherMeta: t("download.launcherMeta"),
      launcherNote: t("download.launcherNote"),
      launcherRepo: t("download.launcherRepo"),
      launcherDownload: t("download.launcherDownload"),
      launcherUnavailable: t("download.launcherUnavailable"),
      latest: t("download.latest"),
      windows: t("download.windows"),
      windowsArm: t("download.windowsArm"),
      dockerTab: t("download.dockerTab"),
      nav: t("download.nav"),
      title: t("download.title"),
      subtitle: t("download.subtitle"),
      windowsMeta: t("download.windowsMeta"),
      windowsArmMeta: t("download.windowsArmMeta"),
      windowsNote: t("download.windowsNote"),
      windowsArmNote: t("download.windowsArmNote"),
      installTitle: t("download.installTitle"),
      installLine: t("download.installLine"),
      startLine: t("download.startLine"),
      returnLine: t("download.returnLine"),
      scriptTab: t("download.scriptTab"),
      scriptDescription: t("download.scriptDescription"),
      dockerDescription: t("download.dockerDescription"),
      scriptCommand: t("download.scriptCommand"),
      dockerCommand: t("download.dockerCommand"),
      guide: t("download.guide"),
      download: t("download.download"),
    },
    run: {
      linux: t("run.linux"),
      start: t("run.start"),
      windows: t("run.windows"),
    },
  }
}
