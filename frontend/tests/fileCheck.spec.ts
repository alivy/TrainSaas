import { describe, it, expect } from 'vitest'
import { validateImageFile } from '@/utils/fileCheck'

function makeFile(name: string, type: string, size: number): File {
  return new File([new Uint8Array(size)], name, { type })
}

describe('validateImageFile（凭据/图片上传前端预检）', () => {
  it('合法 png/jpg/jpeg/webp 通过', () => {
    expect(validateImageFile(makeFile('a.png', 'image/png', 1024))).toBeNull()
    expect(validateImageFile(makeFile('a.jpg', 'image/jpeg', 1024))).toBeNull()
    expect(validateImageFile(makeFile('a.jpeg', 'image/jpeg', 1024))).toBeNull()
    expect(validateImageFile(makeFile('a.webp', 'image/webp', 1024))).toBeNull()
  })

  it('非图片类型拒绝', () => {
    expect(validateImageFile(makeFile('a.txt', 'text/plain', 10))).toBe('仅支持 jpg/png/webp 图片')
  })

  it('超过 5MB 拒绝', () => {
    expect(validateImageFile(makeFile('a.png', 'image/png', 5 * 1024 * 1024 + 1))).toBe(
      '图片不能超过 5MB'
    )
  })

  it('恰好 5MB 通过', () => {
    expect(validateImageFile(makeFile('a.png', 'image/png', 5 * 1024 * 1024))).toBeNull()
  })

  it('扩展名与类型不匹配拒绝', () => {
    expect(validateImageFile(makeFile('a.txt', 'image/png', 10))).toBe('文件扩展名与图片类型不匹配')
    expect(validateImageFile(makeFile('a.png', 'image/jpeg', 10))).toBe('文件扩展名与图片类型不匹配')
  })
})
