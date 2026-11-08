import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { createI18n } from 'vue-i18n'

const codes = ['zh-CN', 'zh-TW', 'ko', 'en']
const messages = Object.fromEntries(codes.map(code => [code,
  JSON.parse(readFileSync(new URL(`../.vitepress/theme/locales/${code}.json`, import.meta.url), 'utf8'))]))

function leaves(value, prefix = '') {
  return Object.entries(value).flatMap(([key, item]) => {
    const name = prefix ? `${prefix}.${key}` : key
    return typeof item === 'string' ? [[name, item]] : leaves(item, name)
  })
}

test('each route locale resolves every label to text without cross-language fallback', () => {
  const i18n = createI18n({ legacy: false, locale: 'en', fallbackLocale: false, messages })
  const expected = leaves(messages.en).map(([name]) => name).sort()
  for (const code of codes) {
    i18n.global.locale.value = code
    const entries = leaves(messages[code])
    assert.deepEqual(entries.map(([name]) => name).sort(), expected, code)
    for (const [name, value] of entries) assert.equal(i18n.global.t(name), value, `${code}: ${name}`)
    assert.equal(messages[code].pages.length, 12, code)
    assert.ok(i18n.global.t('download.launcherDownload').length > 0)
    assert.ok(i18n.global.t('releases.title').length > 0)
  }
})

test('separate documentation renders retain their own locale', () => {
  const english = createI18n({ legacy: false, locale: 'en', messages })
  const korean = createI18n({ legacy: false, locale: 'ko', messages })
  korean.global.locale.value = 'zh-TW'
  assert.equal(english.global.t('docs'), messages.en.docs)
  assert.equal(korean.global.t('docs'), messages['zh-TW'].docs)
})
