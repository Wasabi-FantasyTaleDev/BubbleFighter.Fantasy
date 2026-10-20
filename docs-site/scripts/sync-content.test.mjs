import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { locales, rewriteLinks, syncContent, validatePages } from './sync-content.mjs'

const pages = [{ source: 'installation', route: 'getting-started/installation' }, { source: 'plugins', route: 'guides/plugins' }]

test('publication rejects traversal, duplicates and extra inputs', () => {
  for (const bad of [[], [{ source: '../private/secret', route: 'guides/secret' }],
    [{ source: 'private/secret', route: 'guides/secret' }], [...pages, pages[0]],
    [{ source: 'valid', route: '../secret' }], [{ ...pages[0], input: '../docs/private' }]]) {
    assert.throws(() => validatePages(bad))
  }
})

test('links resolve within the locale and cannot import a private document', () => {
  assert.equal(rewriteLinks('[Plugin](plugins.md#lifetime)', pages[0].route, pages), '[Plugin](../guides/plugins#lifetime)')
  assert.equal(rewriteLinks('[URL](https://example.org/plugins.md)', pages[0].route, pages), '[URL](https://example.org/plugins.md)')
  for (const path of ['../private/secret.md', '/private/secret.md', 'missing.md', '%2e%2e/private.md']) {
    assert.throws(() => rewriteLinks(`[secret](${path})`, pages[0].route, pages))
  }
})

test('all languages are preflighted before touching an existing output', () => {
  const root = mkdtempSync(join(tmpdir(), 'fantasy-docs-'))
  try {
    const site = join(root, 'docs-site'), docs = join(root, 'docs')
    mkdirSync(join(site, 'content'), { recursive: true }); mkdirSync(docs)
    writeFileSync(join(site, 'pages.json'), JSON.stringify(pages))
    const sentinel = join(site, 'content', 'old.md')
    writeFileSync(sentinel, 'preserve on failure')
    mkdirSync(join(site, 'content/public'))
    const publicAsset = join(site, 'content/public', 'asset.md')
    writeFileSync(publicAsset, 'preserve asset')
    const metadata = '---\ntitle: Example\ndescription: Example guide\nupdated: 2026-10-15\n---\n'
    for (const locale of locales.slice(0, -1)) {
      for (const page of pages) writeFileSync(join(docs, page.source + locale.suffix), metadata)
    }
    assert.throws(() => syncContent(site), /Missing public guide/)
    assert.equal(readFileSync(sentinel, 'utf8'), 'preserve on failure')
    for (const page of pages) writeFileSync(join(docs, page.source + '.md'), metadata)
    mkdirSync(join(site, 'content/index.md'))
    assert.throws(() => syncContent(site), /Output is not a regular file/)
    assert.equal(readFileSync(sentinel, 'utf8'), 'preserve on failure')
    rmSync(join(site, 'content/index.md'), { recursive: true })
    assert.deepEqual(syncContent(site), { pages: 2, locales: 4, generated: 12 })
    assert.equal(readFileSync(publicAsset, 'utf8'), 'preserve asset')
    assert.match(readFileSync(join(site, 'content/en/guides/plugins.md'), 'utf8'), /title: Example/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})
