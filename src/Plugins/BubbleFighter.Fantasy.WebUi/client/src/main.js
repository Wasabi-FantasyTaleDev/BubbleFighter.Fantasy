import './noah/styles/tokens.css'
import './noah/styles/glass.css'
import './noah/styles/base.css'
import './styles/app.css'
import './styles/setup.css'

import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { useUiStore } from './stores/ui'

const app = createApp(App)
const pinia = createPinia()
app.use(pinia)
app.use(router)
useUiStore(pinia).init()
app.mount('#app')
