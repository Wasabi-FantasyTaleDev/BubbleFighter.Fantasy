<script setup>
import odteamMark from '../assets/icon_odteam.png'
import { ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahCard from '../noah/components/ui/NoahCard.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import { useServerStore } from '../stores/server'
import { useI18n } from '../i18n'

const { t } = useI18n()
const server = useServerStore()
const username = ref('')
const password = ref('')
const busy = ref(false)
const error = ref('')

// Administrators sign in by account; the server keeps the session in an HTTP-only cookie.
async function submit() {
  if (busy.value || !username.value || !password.value) return
  busy.value = true
  error.value = ''
  try {
    await server.login(username.value, password.value)
    password.value = ''
  } catch (e) {
    error.value = e.status === 429 ? t('setup.rateLimited') : e.status === 401 ? t('setup.loginRejected') : t('signin.failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="signin">
    <NoahCard class="signin-card" body-padding="28px">
      <form class="signin-form" @submit.prevent="submit">
        <div class="signin-brand">
          <img class="brand-mark" :src="odteamMark" alt="" width="22" height="22">
          <span class="brand-text">Bubble Fighter <b>Fantasy</b></span>
        </div>
        <h1 class="signin-title">{{ t('signin.title') }}</h1>
        <p class="signin-desc">{{ t('setup.loginDescription') }}</p>
        <label for="login-username">{{ t('setup.username') }}</label>
        <NoahInput id="login-username" v-model="username" name="username" autocomplete="username" required maxlength="64" />
        <label for="login-password">{{ t('setup.password') }}</label>
        <NoahInput id="login-password" v-model="password" name="password" type="password" autocomplete="current-password" required maxlength="256" :show-password-label="t('setup.showPassword')" :hide-password-label="t('setup.hidePassword')" />
        <p v-if="error" class="signin-error" role="alert">{{ error }}</p>
        <NoahButton variant="primary" html-type="submit" :loading="busy" :disabled="!username || !password">{{ t('signin.submit') }}</NoahButton>
      </form>
    </NoahCard>
  </div>
</template>
