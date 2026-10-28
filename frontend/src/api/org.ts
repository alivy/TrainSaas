import { orgApi } from './http'
import type {
  Paged,
  StaffLoginResponse,
  StaffMeResponse,
  StaffListItem,
  StaffCreatedResponse,
  CreateStaffRequest,
  UpdateStaffRequest,
  Role,
  ProductItem,
  ProductType,
  CreateProductRequest,
  UpdateProductRequest,
  StudentItem,
  StudentDetail,
  StudentResponse,
  CreateStudentRequest,
  UpdateStudentRequest,
  IntentListItem,
  IntentStatus,
  OrderListItem,
  OrderDetail,
  OrderStatus,
  CreateOrderRequest,
  OrderCreatedResponse,
  VoidOrderRequest,
  VoucherCreatedResponse,
  UploadFileResponse,
  TemplateListItem,
  TemplateResponse,
  CreateTemplateRequest,
  UpdateTemplateRequest,
  RosterItem,
  EnrollmentCreatedResponse,
  SessionListItem,
  SessionDetail,
  MarkItemWire,
  AttendanceResult,
  ConsumptionItem,
  ConsumptionKind,
  WalkInResult,
  ReverseResult,
  SalesReportRow,
  TeacherReportRow,
  OrgProfileResponse,
  UpdateOrgProfileRequest
} from './types'

// ===== 认证 =====
export function orgLogin(phone: string, password: string) {
  return orgApi.post<StaffLoginResponse>('/api/org/auth/login', { phone, password }).then((r) => r.data)
}

export function orgMe() {
  return orgApi.get<StaffMeResponse>('/api/org/auth/me').then((r) => r.data)
}

// ===== 员工 =====
export function orgStaffList(params: { role?: Role; page?: number; pageSize?: number }) {
  return orgApi.get<Paged<StaffListItem>>('/api/org/staff', { params }).then((r) => r.data)
}

export function orgStaffCreate(req: CreateStaffRequest) {
  return orgApi.post<StaffCreatedResponse>('/api/org/staff', req).then((r) => r.data)
}

export function orgStaffUpdate(id: number, req: UpdateStaffRequest) {
  return orgApi.put(`/api/org/staff/${id}`, req).then(() => undefined)
}

export function orgStaffSetActive(id: number, active: boolean) {
  return orgApi.post(`/api/org/staff/${id}/${active ? 'activate' : 'deactivate'}`).then(() => undefined)
}

export function orgStaffResetPassword(id: number) {
  return orgApi.post(`/api/org/staff/${id}/reset-password`).then(() => undefined)
}

// ===== 产品 =====
export function orgProductsList(params: {
  type?: ProductType
  published?: boolean
  page?: number
  pageSize?: number
}) {
  return orgApi.get<Paged<ProductItem>>('/api/org/products', { params }).then((r) => r.data)
}

export function orgProductCreate(req: CreateProductRequest) {
  return orgApi.post<ProductItem>('/api/org/products', req).then((r) => r.data)
}

export function orgProductUpdate(id: number, req: UpdateProductRequest) {
  return orgApi.put(`/api/org/products/${id}`, req).then(() => undefined)
}

export function orgProductSetPublished(id: number, published: boolean) {
  return orgApi.post(`/api/org/products/${id}/${published ? 'publish' : 'unpublish'}`).then(() => undefined)
}

// ===== 学员 =====
export function orgStudentsList(params: { q?: string; phone?: string; page?: number; pageSize?: number }) {
  return orgApi.get<Paged<StudentItem>>('/api/org/students', { params }).then((r) => r.data)
}

export function orgStudentDetail(id: number) {
  return orgApi.get<StudentDetail>(`/api/org/students/${id}`).then((r) => r.data)
}

export function orgStudentCreate(req: CreateStudentRequest) {
  return orgApi.post<StudentResponse>('/api/org/students', req).then((r) => r.data)
}

export function orgStudentUpdate(id: number, req: UpdateStudentRequest) {
  return orgApi.put(`/api/org/students/${id}`, req).then(() => undefined)
}

// ===== 家长密码重置（规格 §6.2）=====
export function orgParentResetPassword(phone: string) {
  return orgApi.post('/api/org/parents/reset-password', { phone }).then(() => undefined)
}

// ===== 线索 =====
export function orgIntentsList(params: { status?: IntentStatus; page?: number; pageSize?: number }) {
  return orgApi.get<Paged<IntentListItem>>('/api/org/intents', { params }).then((r) => r.data)
}

export function orgIntentFollow(id: number) {
  return orgApi.post(`/api/org/intents/${id}/follow`).then(() => undefined)
}

export function orgIntentMarkInvalid(id: number, reason?: string) {
  return orgApi.post(`/api/org/intents/${id}/mark-invalid`, { reason }).then(() => undefined)
}

export function orgIntentMarkConverted(id: number, orderId?: number) {
  return orgApi.post(`/api/org/intents/${id}/mark-converted`, { orderId }).then(() => undefined)
}

// ===== 订单 =====
export function orgOrdersList(params: {
  status?: OrderStatus
  studentId?: number
  page?: number
  pageSize?: number
}) {
  return orgApi
    .get<Paged<OrderListItem>>('/api/org/orders', {
      params: {
        status: params.status,
        studentId: params.studentId,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20
      }
    })
    .then((r) => r.data)
}

export function orgOrderDetail(id: number) {
  return orgApi.get<OrderDetail>(`/api/org/orders/${id}`).then((r) => r.data)
}

export function orgOrderCreate(req: CreateOrderRequest) {
  return orgApi.post<OrderCreatedResponse>('/api/org/orders', req).then((r) => r.data)
}

export function orgOrderConfirm(id: number) {
  return orgApi.post(`/api/org/orders/${id}/confirm`).then(() => undefined)
}

export function orgOrderCancel(id: number) {
  return orgApi.post(`/api/org/orders/${id}/cancel`).then(() => undefined)
}

export function orgOrderVoid(id: number, req: VoidOrderRequest) {
  return orgApi.post(`/api/org/orders/${id}/void`, req).then(() => undefined)
}

export function orgOrderUploadVoucher(id: number, file: File) {
  const fd = new FormData()
  fd.append('file', file)
  return orgApi
    .post<VoucherCreatedResponse>(`/api/org/files/vouchers/${id}`, fd)
    .then((r) => r.data)
}

// ===== 文件 =====
export function orgUploadImage(file: File, purpose: 'logo' | 'cover') {
  const fd = new FormData()
  fd.append('file', file)
  fd.append('purpose', purpose)
  return orgApi.post<UploadFileResponse>('/api/org/files', fd).then((r) => r.data)
}

// ===== 班级模板 / 报名 =====
export function orgTemplatesList(params: { productId?: number; page?: number; pageSize?: number }) {
  return orgApi.get<Paged<TemplateListItem>>('/api/org/templates', { params }).then((r) => r.data)
}

export function orgTemplateCreate(req: CreateTemplateRequest) {
  return orgApi.post<TemplateResponse>('/api/org/templates', req).then((r) => r.data)
}

export function orgTemplateUpdate(id: number, req: UpdateTemplateRequest) {
  return orgApi.put(`/api/org/templates/${id}`, req).then(() => undefined)
}

export function orgTemplateSetActive(id: number, active: boolean) {
  return orgApi.post(`/api/org/templates/${id}/${active ? 'activate' : 'deactivate'}`).then(() => undefined)
}

export function orgTemplateEnroll(id: number, studentId: number) {
  return orgApi
    .post<EnrollmentCreatedResponse>(`/api/org/templates/${id}/enrollments`, { studentId })
    .then((r) => r.data)
}

export function orgTemplateWithdraw(id: number, studentId: number) {
  return orgApi.delete(`/api/org/templates/${id}/enrollments/${studentId}`).then(() => undefined)
}

export function orgTemplateRoster(id: number) {
  return orgApi.get<RosterItem[]>(`/api/org/templates/${id}/enrollments`).then((r) => r.data)
}

// ===== 课程实例 / 点名 =====
export function orgSessionsList(from: string, to: string) {
  return orgApi
    .get<SessionListItem[]>('/api/org/sessions', { params: { from, to } })
    .then((r) => r.data)
}

export function orgSessionDetail(id: number) {
  return orgApi.get<SessionDetail>(`/api/org/sessions/${id}`).then((r) => r.data)
}

export function orgMarkAttendance(id: number, marks: MarkItemWire[]) {
  return orgApi
    .post<AttendanceResult>(`/api/org/sessions/${id}/attendance`, { marks })
    .then((r) => r.data)
}

// ===== 核销流水 =====
export function orgConsumptionsList(params: {
  studentId?: number
  from?: string
  to?: string
  kind?: ConsumptionKind
  page?: number
  pageSize?: number
}) {
  return orgApi
    .get<Paged<ConsumptionItem>>('/api/org/consumptions', { params })
    .then((r) => r.data)
}

export function orgWalkIn(studentId: number, sessionPackId: number) {
  return orgApi
    .post<WalkInResult>('/api/org/consumptions/walk-in', { studentId, sessionPackId })
    .then((r) => r.data)
}

export function orgReverseConsumption(id: number, reason: string) {
  return orgApi
    .post<ReverseResult>(`/api/org/consumptions/${id}/reverse`, { reason })
    .then((r) => r.data)
}

// ===== 报表 =====
export function orgSalesReport(from: string, to: string) {
  return orgApi
    .get<SalesReportRow[]>('/api/org/reports/sales', { params: { from, to } })
    .then((r) => r.data)
}

export function orgTeachersReport(from: string, to: string) {
  return orgApi
    .get<TeacherReportRow[]>('/api/org/reports/teachers', { params: { from, to } })
    .then((r) => r.data)
}

// ===== 机构页设置 =====
export function orgProfileGet() {
  return orgApi.get<OrgProfileResponse>('/api/org/profile').then((r) => r.data)
}

export function orgProfilePut(req: UpdateOrgProfileRequest) {
  return orgApi.put('/api/org/profile', req).then(() => undefined)
}
