import { lstatSync, readFileSync, readdirSync, unlinkSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, posix, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export const locales = [
  { directory: '', suffix: '.zh.md' },
  { directory: 'zh-tw', suffix: '.zh-tw.md' },
  { directory: 'ko', suffix: '.ko.md' },
  { directory: 'en', suffix: '.md' },
]

/** Refuse traversal, duplicate outputs and undeclared metadata before reading any guide. */
export function validatePages(pages) {
  if (!Array.isArray(pages) || !pages.length) throw new Error('A nonempty page allowlist is required')
  const sources = new Set(), routes = new Set()
  for (const page of pages) {
    if (!page || Object.keys(page).sort().join(',') !== 'route,source'
        || !/^[a-z][a-z0-9-]*$/.test(page.source)
        || !/^(getting-started|guides|project)\/[a-z][a-z0-9-]*$/.test(page.route)
        || sources.has(page.source) || routes.has(page.route)) {
      throw new Error('Invalid or duplicate public page')
    }
    sources.add(page.source)
    routes.add(page.route)
  }
}

/** Rewrite only flat links to explicitly published guides; preserve external URLs and anchors. */
export function rewriteLinks(markdown, route, pages) {
  const routes = new Map(pages.map(page => [page.source, page.route]))
  return markdown.replace(/\]\(([^)]+)\)/g, (full, target) => {
    if (/^(?:[a-z][a-z0-9+.-]*:|#)/i.test(target)) return full
    const match = target.match(/^(?:\.\/)?([a-z][a-z0-9-]*)(?:\.zh-tw|\.zh|\.ko)?\.md([#?].*)?$/)
    if (!match || !routes.has(match[1])) throw new Error(`Unpublished local link: ${target}`)
    const relative = posix.relative(posix.dirname(route), routes.get(match[1]))
    return `](${relative.startsWith('.') ? relative : `./${relative}`}${match[2] || ''})`
  })
}

function rejectLink(path) {
  const info = lstatSync(path, { throwIfNoEntry: false })
  if (info?.isSymbolicLink()) throw new Error(`Linked content is not a publication input: ${path}`)
  return info
}

/** Preflight every input and output before replacing generated Markdown; public assets stay separate. */
export function syncContent(siteRoot) {
  const docsRoot = join(resolve(siteRoot, '..'), 'docs')
  const contentRoot = join(siteRoot, 'content')
  rejectLink(docsRoot)
  rejectLink(contentRoot)
  const pages = JSON.parse(readFileSync(join(siteRoot, 'pages.json'), 'utf8'))
  validatePages(pages)
  const planned = new Map()
  for (const locale of locales) {
    planned.set(join(contentRoot, locale.directory, 'index.md'), '---\nlayout: home\ntitle: Bubble Fighter Fantasy\n---\n')
    for (const page of pages) {
      const source = join(docsRoot, `${page.source}${locale.suffix}`)
      if (!rejectLink(source)?.isFile()) throw new Error(`Missing public guide: ${source}`)
      const text = readFileSync(source, 'utf8')
      if (!/^---\r?\n[\s\S]*?\btitle: [^\r\n]+[\s\S]*?\bdescription: [^\r\n]+[\s\S]*?\bupdated: \d{4}-\d{2}-\d{2}\r?\n---/.test(text)) {
        throw new Error(`Missing guide metadata: ${source}`)
      }
      planned.set(join(contentRoot, locale.directory, `${page.route}.md`), rewriteLinks(text, page.route, pages))
    }
  }
  const stale = []
  function visit(directory) {
    if (!rejectLink(directory)) return
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name)
      if (directory === contentRoot && entry.name === 'public') continue
      rejectLink(path)
      if (entry.isDirectory()) visit(path)
      else if (entry.name.endsWith('.md') && !planned.has(path)) stale.push(path)
    }
  }
  visit(contentRoot)
  for (const destination of planned.keys()) {
    const existing = rejectLink(destination)
    if (existing && !existing.isFile()) throw new Error(`Output is not a regular file: ${destination}`)
    let parent = destination
    while (parent !== contentRoot) {
      if (parent !== destination) {
        const existing = rejectLink(parent)
        if (existing && !existing.isDirectory()) throw new Error(`Output parent is not a directory: ${parent}`)
      }
      parent = dirname(parent)
    }
  }
  for (const path of stale) unlinkSync(path)
  for (const [path, text] of planned) {
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, text, 'utf8')
  }
  return { pages: pages.length, locales: locales.length, generated: planned.size }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(JSON.stringify(syncContent(resolve(dirname(fileURLToPath(import.meta.url)), '..'))))
}
