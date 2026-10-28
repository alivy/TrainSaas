import { publicApi } from './http'
import type { PublicOrgResponse, CreateIntentRequest, IntentCreatedResponse } from './types'

export function publicOrgPage(code: string) {
  return publicApi.get<PublicOrgResponse>(`/api/public/orgs/${code}`).then((r) => r.data)
}

export function publicSubmitIntent(code: string, req: CreateIntentRequest) {
  return publicApi
    .post<IntentCreatedResponse>(`/api/public/orgs/${code}/intents`, req)
    .then((r) => r.data)
}
