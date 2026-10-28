<script setup lang="ts">
import { enumLabel, enumTagType } from '@/utils/format'
import type { EnumKind } from '@/api/types'

const props = defineProps<{ kind: EnumKind; value: string | null | undefined }>()
</script>

<template>
  <el-tag v-if="props.value" :type="enumTagType(props.kind, props.value)" size="small">
    {{ enumLabel(props.kind, props.value) }}
  </el-tag>
  <span v-else>—</span>
</template>
