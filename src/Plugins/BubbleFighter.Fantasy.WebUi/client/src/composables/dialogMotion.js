// Opens and closes a native <dialog class="bff-pop"> with the pop animation in styles/motion.css. The browser closes a
// dialog at once, so the closing class plays the exit first and close() follows when it ends (or after a short bound).

const reduced = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

export function openDialog(dialog) {
  if (!dialog || dialog.open) return
  dialog.classList.remove('is-closing')
  dialog.showModal()
}

export function closeDialog(dialog) {
  if (!dialog?.open || dialog.classList.contains('is-closing')) return Promise.resolve()
  if (reduced()) { dialog.close(); return Promise.resolve() }
  dialog.classList.add('is-closing')
  return new Promise(resolve => {
    let done = false
    const finish = () => {
      if (done) return
      done = true
      dialog.removeEventListener('animationend', onEnd)
      dialog.classList.remove('is-closing')
      dialog.close()
      resolve()
    }
    const onEnd = event => { if (event.target === dialog) finish() }
    dialog.addEventListener('animationend', onEnd)
    setTimeout(finish, 260)
  })
}

// Escape fires cancel and would close without the exit animation; play it instead.
export function animatedCancel(event) {
  event.preventDefault()
  return closeDialog(event.currentTarget)
}
