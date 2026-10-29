import { describe, it, expect, vi, beforeEach } from 'vitest'
import { useMediaNarrow } from '@/utils/useMediaNarrow'

type Listener = (e: { matches: boolean }) => void

function mockMatchMedia(initial: boolean) {
  let listener: Listener | null = null
  const mql = {
    matches: initial,
    media: '(max-width: 767px)',
    addEventListener: (_ev: string, l: Listener) => {
      listener = l
    },
    removeEventListener: vi.fn(() => {
      listener = null
    })
  }
  window.matchMedia = vi.fn().mockReturnValue(mql) as never
  return {
    mql,
    fire(matches: boolean) {
      mql.matches = matches
      listener?.({ matches })
    },
    get hasListener() {
      return listener !== null
    }
  }
}

describe('useMediaNarrow（响应式窄屏判定，C1 修复基建）', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('初始值来自 matchMedia，桌面为 false', () => {
    mockMatchMedia(false)
    const isNarrow = useMediaNarrow()
    expect(isNarrow.value).toBe(false)
  })

  it('初始值来自 matchMedia，窄屏为 true', () => {
    mockMatchMedia(true)
    const isNarrow = useMediaNarrow()
    expect(isNarrow.value).toBe(true)
  })

  it('断点穿越时实时更新', () => {
    const mm = mockMatchMedia(false)
    const isNarrow = useMediaNarrow()
    mm.fire(true)
    expect(isNarrow.value).toBe(true)
    mm.fire(false)
    expect(isNarrow.value).toBe(false)
  })

  it('查询串固定为 (max-width: 767px)', () => {
    mockMatchMedia(false)
    useMediaNarrow()
    expect(window.matchMedia).toHaveBeenCalledWith('(max-width: 767px)')
  })
})
