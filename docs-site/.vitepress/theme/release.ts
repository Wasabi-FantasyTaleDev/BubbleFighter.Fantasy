import { onMounted, ref, type Ref } from 'vue'

/** One downloadable file of a release. */
export interface ReleaseAsset {
  name: string
  url: string
}

/** One published release as the site shows it. */
export interface PublicRelease {
  tag: string
  name: string
  date: string
  url: string
  html: string
  assets: ReleaseAsset[]
}

export const serverRepository = 'Wasabi-FantasyTaleDev/BubbleFighter.Fantasy'
export const launcherRepository = 'Wasabi-FantasyTaleDev/FantasyTale.Launcher'
const cacheMilliseconds = 30 * 60 * 1000

// One shared state per repository, so a page asks GitHub at most once per repository; null until loaded,
// [] when unavailable (private repository, no release yet, offline, or the anonymous rate limit was hit).
const states = new Map<string, { releases: Ref<PublicRelease[] | null>; loading?: Promise<void> }>()

function stateOf(repository: string) {
  let state = states.get(repository)
  if (!state) states.set(repository, state = { releases: ref<PublicRelease[] | null>(null) })
  return state
}

function readCache(key: string): PublicRelease[] | undefined {
  try {
    const cached = JSON.parse(localStorage.getItem(key) || 'null')
    if (cached && Date.now() - cached.at < cacheMilliseconds && Array.isArray(cached.releases)) return cached.releases
  } catch { /* storage blocked or malformed: fetch again */ }
  return undefined
}

async function load(repository: string, releases: Ref<PublicRelease[] | null>) {
  const key = `bff-releases-v2:${repository}`
  const cached = readCache(key)
  if (cached) { releases.value = cached; return }
  try {
    // The html media type returns GitHub's own sanitized rendering of each release body.
    const response = await fetch(`https://api.github.com/repos/${repository}/releases?per_page=10`, {
      headers: { Accept: 'application/vnd.github.html+json' },
    })
    if (!response.ok) { releases.value = []; return }
    const rows = await response.json()
    releases.value = (Array.isArray(rows) ? rows : [])
      .filter((row: any) => !row.draft && !row.prerelease && typeof row.tag_name === 'string')
      .map((row: any) => ({
        tag: row.tag_name,
        name: row.name || row.tag_name,
        date: String(row.published_at || '').slice(0, 10),
        url: row.html_url,
        html: typeof row.body_html === 'string' ? row.body_html : '',
        assets: (Array.isArray(row.assets) ? row.assets : [])
          .filter((asset: any) => typeof asset.name === 'string' && typeof asset.browser_download_url === 'string')
          .map((asset: any) => ({ name: asset.name, url: asset.browser_download_url })),
      }))
    try { localStorage.setItem(key, JSON.stringify({ at: Date.now(), releases: releases.value })) } catch { /* ignore */ }
  } catch {
    releases.value = []
  }
}

/** Published releases of a repository, newest first. The browser-side load starts in onMounted, so the
 * server-rendered page and its hydration both begin from the empty state. */
export function usePublicReleases(repository: string = serverRepository) {
  const state = stateOf(repository)
  onMounted(() => { state.loading ??= load(repository, state.releases) })
  return state.releases
}

/** Select a Windows-compatible x64 download without mistaking another platform's archive for it.
 * Prefer an explicit Windows label; retain platform-neutral x64 names used by Windows-only releases.
 * Unqualified archives remain unavailable because their platform and architecture cannot be inferred. */
export function windowsX64Asset(release: PublicRelease | undefined): ReleaseAsset | undefined {
  const files = (release?.assets || []).filter(asset => {
    const name = asset.name.replace(/x86[_-]64/ig, 'x64')
    return /\.(zip|exe|msi)$/i.test(name)
      && !/linux|darwin|macos|osx|android|freebsd|aarch64|arm64|armv\d|win32|ia32/i.test(name)
      && !/(?:^|[-_. ])(?:arm|x86|i[3-6]86|mac|ios)(?:[-_. ]|$)/i.test(name)
  })
  return files.find(asset => /(?:^|[-_. ])win(?:dows)?(?:[-_. ]|$)/i.test(asset.name))
    || files.find(asset => /(?:^|[-_. ])(?:x64|amd64|x86[_-]64)(?:[-_. ]|$)/i.test(asset.name))
}
