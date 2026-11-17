<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from './TpIcon.vue'
import { osText } from './os'
import type { Code } from '../types'

// The Windows "Select Folder" dialog an app opens to pick a workspace. The scene drives it from its
// clock: `created` shows the new row, `typed` is the name typed so far, `named` commits it.
const props = defineProps({
  lang: { type: String as PropType<Code>, required: true },
  newPressed: Boolean,
  created: Boolean,
  typed: { type: String, default: '' },
  named: Boolean,
  okPressed: Boolean,
  name: { type: String, default: 'BubbleFighterFantasy' },
})
const os = computed(() => osText(props.lang))
const folders = ['Games', 'Notes', 'Photos backup', 'Projects', 'Work']
</script>

<template>
  <div class="wb-folder">
    <div class="wb-folder-title"><slot name="icon" />{{ os.pickTitle }}<TpIcon name="close" :size="15" /></div>
    <div class="ex-address"><TpIcon name="back" :size="15" /><TpIcon name="forward" :size="15" /><TpIcon name="up" :size="15" /><div class="ex-path"><TpIcon name="file" :size="14" />› {{ os.documents }}</div><div class="ex-search">{{ os.searchIn(os.documents) }}<TpIcon name="search" :size="14" /></div></div>
    <div class="wb-folder-tools"><span>{{ os.organize }} ⌄</span><span data-tp="fd-new" :class="{ 'is-pressed': newPressed }">{{ os.newFolder }}</span><span class="wb-spacer" /><TpIcon name="view" :size="16" /></div>
    <div class="ex-body">
      <ul class="ex-nav"><li><TpIcon name="home" :size="15" />{{ os.home }}</li><li><TpIcon name="desktop" :size="15" />{{ os.desktop }}</li><li><TpIcon name="download" :size="15" />{{ os.downloads }}</li><li class="is-current"><TpIcon name="file" :size="15" />{{ os.documents }}</li><li><TpIcon name="picture" :size="15" />{{ os.pictures }}</li><li><TpIcon name="music" :size="15" />{{ os.music }}</li><li class="ex-gap"><TpIcon name="pc" :size="15" />{{ os.thisPc }}</li></ul>
      <div class="ex-list">
        <div class="ex-head"><span>{{ os.name }}</span><span>{{ os.modified }}</span><span>{{ os.type }}</span><span /></div>
        <div v-if="created" class="ex-row is-new" data-tp="fd-newrow" :class="{ 'is-selected': named }">
          <span><TpIcon name="folder" :size="16" /><em v-if="!named" class="ex-rename">{{ typed || os.newFolder }}<i class="tp-caret" /></em><template v-else>{{ name }}</template></span><span>{{ os.newStamp }}</span><span>{{ os.folder }}</span><span />
        </div>
        <div v-for="item in folders" :key="item" class="ex-row"><span><TpIcon name="folder" :size="16" />{{ item }}</span><span>{{ os.oldStamp }}</span><span>{{ os.folder }}</span><span /></div>
      </div>
    </div>
    <div class="wb-folder-foot"><label>{{ os.folderLabel }}</label><span class="wb-folder-field">{{ named ? name : '' }}</span><span class="wb-folder-ok" data-tp="fd-ok" :class="{ 'is-pressed': okPressed }">{{ os.pickOk }}</span><span class="wb-folder-cancel">{{ os.cancel }}</span></div>
  </div>
</template>
