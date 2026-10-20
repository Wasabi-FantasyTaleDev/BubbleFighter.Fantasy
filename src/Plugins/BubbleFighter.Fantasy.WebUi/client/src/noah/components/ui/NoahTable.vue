<template>
  <div ref="container" class="noah-table-container" :class="{ 'has-more': hasMore }" @scroll.passive="measure">
    <table class="noah-table">
      <thead>
        <tr>
          <th
            v-for="col in columns"
            :key="col.key"
            :style="{ width: col.width, textAlign: col.align || 'left' }"
            :class="{ 'is-sortable': col.sortable }"
            @click="handleSort(col)"
          >
            <div class="noah-th-content" :style="{ justifyContent: col.align === 'right' ? 'flex-end' : col.align === 'center' ? 'center' : 'flex-start' }">
              <span>{{ col.title }}</span>
              <span v-if="col.sortable" class="noah-sort-indicator">
                <NoahIcon
                  v-if="sortKey === col.key && sortOrder === 'asc'"
                  name="chevron-up"
                  :size="10"
                />
                <NoahIcon
                  v-else-if="sortKey === col.key && sortOrder === 'desc'"
                  name="chevron-down"
                  :size="10"
                />
                <NoahIcon
                  v-else
                  name="chevron-down"
                  :size="10"
                  class="opacity-30"
                />
              </span>
            </div>
          </th>
        </tr>
      </thead>

      <tbody>
        <tr v-if="loading" class="noah-table-loading-row">
          <td :colspan="columns.length" class="text-center py-6 text-muted">
            <span class="noah-table-spinner" />
            <span class="ml-2">{{ loadingText }}</span>
          </td>
        </tr>

        <tr v-else-if="!data || data.length === 0" class="noah-table-empty-row">
          <td :colspan="columns.length" class="text-center py-8 text-faint">
            {{ emptyText }}
          </td>
        </tr>

        <tr
          v-for="(row, idx) in visibleData"
          v-else
          :key="row.id || idx"
          class="noah-table-row"
          :class="{ 'is-clickable': rowClickable }"
          @click="emit('row-click', row)"
        >
          <td
            v-for="col in columns"
            :key="col.key"
            :style="{ textAlign: col.align || 'left' }"
          >
            <slot :name="col.key" :row="row" :index="idx">
              {{ row[col.key] }}
            </slot>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  columns: {
    type: Array,
    default: () => [] // [{ title: '', key: '', width: '', align: 'left', sortable: false }]
  },
  data: {
    type: Array,
    default: () => []
  },
  loading: {
    type: Boolean,
    default: false
  },
  loadingText: {
    type: String,
    default: 'Loading data...'
  },
  emptyText: {
    type: String,
    default: 'No records found'
  },
  rowClickable: {
    type: Boolean,
    default: false
  },
  // Initial sort, `{ key, order: 'asc' | 'desc' }`; the column should be sortable to show its indicator.
  defaultSort: {
    type: Object,
    default: null
  },
  // Rows rendered after sorting; 0 renders all. Sorting sees every row, so a limited table still orders globally.
  limit: {
    type: Number,
    default: 0
  }
})

const emit = defineEmits(['row-click'])

const sortKey = ref(props.defaultSort?.key ?? '')
const sortOrder = ref(props.defaultSort ? props.defaultSort.order ?? 'asc' : '') // 'asc' | 'desc' | ''

const handleSort = (col) => {
  if (!col.sortable) return
  if (sortKey.value !== col.key) {
    sortKey.value = col.key
    sortOrder.value = 'asc'
  } else if (sortOrder.value === 'asc') {
    sortOrder.value = 'desc'
  } else {
    sortKey.value = ''
    sortOrder.value = ''
  }
}

// Numbers compare as numbers, so 602 sorts before 32788; text compares with digit runs as numbers.
function compare(a, b) {
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return String(a).localeCompare(String(b), undefined, { numeric: true })
}

const sortedData = computed(() => {
  if (!sortKey.value || !sortOrder.value) {
    return props.data
  }
  const sign = sortOrder.value === 'asc' ? 1 : -1
  const key = sortKey.value
  // A missing value sorts last in either direction.
  return [...props.data].sort((a, b) => {
    const vA = a[key]
    const vB = b[key]
    if (vA == null || vB == null) return vA == null ? (vB == null ? 0 : 1) : -1
    return sign * compare(vA, vB)
  })
})
const visibleData = computed(() => (props.limit > 0 ? sortedData.value.slice(0, props.limit) : sortedData.value))

// A soft fade on the right edge tells that more columns wait beyond the card when the table overflows.
const container = ref(null)
const hasMore = ref(false)
let observer = null
function measure() {
  const el = container.value
  hasMore.value = !!el && el.scrollWidth - el.clientWidth - el.scrollLeft > 2
}
onMounted(() => {
  measure()
  if (typeof ResizeObserver === 'function') {
    observer = new ResizeObserver(measure)
    observer.observe(container.value)
    const table = container.value.querySelector('table')
    if (table) observer.observe(table)
  }
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<style scoped>
.noah-table-container {
  width: 100%;
  overflow-x: auto;
  border-radius: var(--radius-xs);
  border: 1px solid var(--border);
  background-color: var(--surface);
}

.noah-table-container.has-more {
  -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 36px), transparent);
  mask-image: linear-gradient(to right, #000 calc(100% - 36px), transparent);
}

.noah-table {
  width: 100%;
  border-collapse: collapse;
  font-family: var(--font-ui);
  font-size: var(--text-label);
  text-align: left;
}

.noah-table th {
  padding: 10px 14px;
  background-color: var(--surface-raised);
  color: var(--text-muted);
  font-weight: 600;
  border-bottom: 1px solid var(--border);
  white-space: nowrap;
}

.noah-table th.is-sortable {
  cursor: pointer;
  user-select: none;
}
.noah-table th.is-sortable:hover {
  color: var(--text);
}

.noah-th-content {
  display: flex;
  align-items: center;
  gap: 4px;
}

.noah-sort-indicator {
  display: inline-flex;
}

.noah-table td {
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-soft);
  color: var(--text);
  line-height: var(--leading-body);
}

.noah-table tbody tr:last-child td {
  border-bottom: none;
}

.noah-table-row {
  transition: background-color 0.12s ease;
}

.noah-table-row:hover {
  background-color: var(--hover);
}

.noah-table-row.is-clickable {
  cursor: pointer;
}

.noah-table-spinner {
  display: inline-block;
  width: 14px;
  height: 14px;
  border: 2px solid var(--text-muted);
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.6s linear infinite;
  vertical-align: middle;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
