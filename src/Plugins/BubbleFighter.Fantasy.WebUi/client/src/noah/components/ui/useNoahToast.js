import { ref } from 'vue'

const toasts = ref([])
let toastId = 0

export const useNoahToast = () => {
  const show = ({ type = 'info', title = '', message = '', duration = 3500 }) => {
    const id = ++toastId
    const item = {
      id,
      type, // 'ok' | 'warn' | 'err' | 'info'
      title,
      message,
      duration
    }

    toasts.value.push(item)

    if (duration > 0) {
      setTimeout(() => {
        remove(id)
      }, duration)
    }

    return id
  }

  const ok = (message, title = 'Success', duration = 3000) => {
    return show({ type: 'ok', title, message, duration })
  }

  const warn = (message, title = 'Warning', duration = 4000) => {
    return show({ type: 'warn', title, message, duration })
  }

  const err = (message, title = 'Error', duration = 5000) => {
    return show({ type: 'err', title, message, duration })
  }

  const info = (message, title = 'Info', duration = 3000) => {
    return show({ type: 'info', title, message, duration })
  }

  const remove = (id) => {
    const index = toasts.value.findIndex(t => t.id === id)
    if (index !== -1) {
      toasts.value.splice(index, 1)
    }
  }

  return {
    toasts,
    show,
    ok,
    warn,
    err,
    info,
    remove
  }
}
