import { onScopeDispose, ref } from 'vue'

const NARROW_QUERY = '(max-width: 767px)'

/**
 * 窄屏（<768px）响应式判定（C1 修复基建）。
 * 在组件 setup 作用域内调用；作用域销毁时自动移除监听。
 */
export function useMediaNarrow() {
  const mql = window.matchMedia(NARROW_QUERY)
  const isNarrow = ref(mql.matches)
  const onChange = (e: MediaQueryListEvent | { matches: boolean }) => {
    isNarrow.value = e.matches
  }
  mql.addEventListener('change', onChange)
  onScopeDispose(() => mql.removeEventListener('change', onChange))
  return isNarrow
}
