import { onMounted, onUnmounted, ref } from 'vue'

/**
 * Loads data with `fetcher` and optionally keeps it fresh. The first load shows a skeleton and an
 * error card; later (quiet) refreshes replace the data silently and keep the last good data when
 * they fail. Polling pauses while the tab is hidden and refreshes at once when it returns.
 *
 * @param {() => Promise<any>} fetcher Returns the data.
 * @param {{ interval?: number, immediate?: boolean }} options `interval` in ms; 0 or missing disables polling.
 */
export function useResource(fetcher, { interval = 0, immediate = true } = {}) {
  const data = ref(null)
  const error = ref(null)
  const loading = ref(false)
  const now = ref(Date.now())
  let timer = null
  let seq = 0
  let alive = false

  async function load(quiet = false) {
    const mine = ++seq
    if (!quiet) loading.value = true
    try {
      const result = await fetcher()
      if (mine !== seq) return
      data.value = result
      error.value = null
      now.value = Date.now()
    } catch (e) {
      if (mine !== seq) return
      // A quiet refresh that fails keeps what is on screen; the connection dot reports the outage.
      if (!quiet || data.value === null) error.value = e
    } finally {
      if (mine === seq) loading.value = false
    }
  }

  function schedule() {
    clearTimeout(timer)
    if (!alive || !interval) return
    timer = setTimeout(async () => {
      if (!document.hidden) await load(true)
      schedule()
    }, interval)
  }

  function onVisibility() {
    if (!document.hidden && interval) {
      clearTimeout(timer)
      load(true).finally(schedule)
    }
  }

  onMounted(() => {
    alive = true
    if (immediate) load(false).finally(schedule)
    else schedule()
    document.addEventListener('visibilitychange', onVisibility)
  })
  onUnmounted(() => {
    alive = false
    seq++
    clearTimeout(timer)
    document.removeEventListener('visibilitychange', onVisibility)
  })

  return { data, error, loading, now, load, reload: () => load(false) }
}
