import { parentApi } from './http'
import type {
  Paged,
  ParentLoginResponse,
  ChangePasswordRequest,
  ParentChildItem,
  ParentPackItem,
  ParentRecordItem,
  ParentScheduleItem
} from './types'

export function parentLogin(phone: string, password: string) {
  return parentApi
    .post<ParentLoginResponse>('/api/parent/auth/login', { phone, password })
    .then((r) => r.data)
}

export function parentChangePassword(req: ChangePasswordRequest) {
  return parentApi.post('/api/parent/change-password', req).then(() => undefined)
}

export function parentChildren() {
  return parentApi.get<ParentChildItem[]>('/api/parent/children').then((r) => r.data)
}

export function parentPacks(studentId: number) {
  return parentApi
    .get<ParentPackItem[]>('/api/parent/packs', { params: { studentId } })
    .then((r) => r.data)
}

export function parentRecords(
  studentId: number,
  params: { from?: string; to?: string; page?: number; pageSize?: number }
) {
  return parentApi
    .get<Paged<ParentRecordItem>>('/api/parent/records', {
      params: {
        studentId,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20,
        from: params.from,
        to: params.to
      }
    })
    .then((r) => r.data)
}

export function parentSchedule(studentId: number, days = 14) {
  return parentApi
    .get<ParentScheduleItem[]>('/api/parent/schedule', { params: { studentId, days } })
    .then((r) => r.data)
}
