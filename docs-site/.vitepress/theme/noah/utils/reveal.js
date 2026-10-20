/**
 * Reveal an element once when it enters the viewport. The returned disposer
 * removes observers and listeners, and restores visible content on teardown.
 * Server-rendered content stays visible until a browser can observe it.
 */
export function observeReveal(element) {
  const view = element.ownerDocument.defaultView
  if (!view?.IntersectionObserver || !view.matchMedia) return () => {}
  const preference = view.matchMedia('(prefers-reduced-motion: reduce)')
  if (preference.matches) return () => {}
  let observer
  let disposed = false
  const dispose = () => {
    if (disposed) return
    disposed = true
    observer?.disconnect()
    preference.removeEventListener('change', onPreference)
    element.removeEventListener('focusin', reveal)
    element.classList.remove('noah-reveal-pending')
  }
  const reveal = () => dispose()
  const onPreference = event => { if (event.matches) reveal() }
  observer = new view.IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting)) reveal()
  }, { threshold: 0, rootMargin: '0px 0px -16px 0px' })
  preference.addEventListener('change', onPreference)
  element.addEventListener('focusin', reveal)
  element.classList.add('noah-reveal-pending')
  observer.observe(element)
  return dispose
}
