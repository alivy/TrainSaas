import { platformApi } from './http'
import type {
  Paged,
  PlatformLoginResponse,
  ChangePasswordRequest,
  InstitutionListItem,
  InstitutionStatus,
  CreateInstitutionRequest,
  InstitutionCreatedResponse,
  UpdateInstitutionRequest
} from './types'

export function platformLogin(username: string, password: string) {
  return platformApi
    .post<PlatformLoginResponse>('/api/platform/auth/login', { username, password })
    .then((r) => r.data)
}

export function platformChangePassword(req: ChangePasswordRequest) {
  return platformApi.post('/api/platform/auth/change-password', req).then(() => undefined)
}

export function platformListInstitutions(params: {
  status?: InstitutionStatus
  page?: number
  pageSize?: number
}) {
  return platformApi
    .get<Paged<InstitutionListItem>>('/api/platform/institutions', { params })
    .then((r) => r.data)
}

export function platformCreateInstitution(req: CreateInstitutionRequest) {
  return platformApi
    .post<InstitutionCreatedResponse>('/api/platform/institutions', req)
    .then((r) => r.data)
}

export function platformUpdateInstitution(id: number, req: UpdateInstitutionRequest) {
  return platformApi.put(`/api/platform/institutions/${id}`, req).then(() => undefined)
}

export function platformSetInstitutionStatus(id: number, action: 'disable' | 'enable') {
  return platformApi.post(`/api/platform/institutions/${id}/${action}`).then(() => undefined)
}

export function platformResetAdminPassword(id: number) {
  return platformApi
    .post(`/api/platform/institutions/${id}/reset-admin-password`)
    .then(() => undefined)
}
