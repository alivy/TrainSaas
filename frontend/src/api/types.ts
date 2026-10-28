// ===== 枚举联合类型（与后端 System.Text.Json 枚举名序列化一一对应；查询参数必须传名称字符串，数字串会被后端 400） =====
export type Role = 'Admin' | 'Sales' | 'Teacher' | 'FrontDesk'
export type ProductType = 'Fixed' | 'Flexible'
export type OrderStatus = 'Pending' | 'Active' | 'Cancelled' | 'Voided'
export type PackStatus = 'Active' | 'Voided'
export type InstitutionStatus = 'Active' | 'Disabled'
export type SessionStatus = 'Scheduled' | 'Completed' | 'Cancelled'
export type EnrollmentStatus = 'Enrolled' | 'Withdrawn'
export type Mark = 'Present' | 'Leave' | 'Absent'
export type ConsumptionKind = 'RollCall' | 'WalkIn' | 'Reversal'
export type IntentStatus = 'New' | 'Followed' | 'Converted' | 'Invalid'

// 单一来源：全部枚举成员（format.spec 完整性测试遍历此表）
export const enumValues = {
  role: ['Admin', 'Sales', 'Teacher', 'FrontDesk'],
  productType: ['Fixed', 'Flexible'],
  orderStatus: ['Pending', 'Active', 'Cancelled', 'Voided'],
  packStatus: ['Active', 'Voided'],
  institutionStatus: ['Active', 'Disabled'],
  sessionStatus: ['Scheduled', 'Completed', 'Cancelled'],
  enrollmentStatus: ['Enrolled', 'Withdrawn'],
  mark: ['Present', 'Leave', 'Absent'],
  consumptionKind: ['RollCall', 'WalkIn', 'Reversal'],
  intentStatus: ['New', 'Followed', 'Converted', 'Invalid']
} as const satisfies Record<string, readonly string[]>

export type EnumKind = keyof typeof enumValues

// ===== 通用 =====
export interface Paged<T> {
  total: number
  items: T[]
}

// ===== 认证 =====
export interface PlatformLoginResponse { token: string; name: string }
export interface StaffLoginResponse {
  token: string
  name: string
  role: Role
  institutionId: number
  institutionName: string
  institutionCode: string
}
export type StaffProfile = Omit<StaffLoginResponse, 'token'>
export interface StaffMeResponse {
  staffId: number
  name: string
  role: Role
  institutionId: number
  institutionName: string
  institutionCode: string
}
export interface ParentLoginResponse { token: string; phone: string }
export interface ChangePasswordRequest { oldPassword: string; newPassword: string }

// ===== 平台机构 =====
export interface InstitutionListItem {
  id: number
  code: string
  name: string
  status: InstitutionStatus
  staffCount: number
  studentCount: number
  createdAt: string
}
export interface CreateInstitutionRequest {
  code: string
  name: string
  adminName: string
  adminPhone: string
  contactPhone?: string
  address?: string
  introText?: string
}
export interface InstitutionCreatedResponse { id: number; code: string; name: string }
export interface UpdateInstitutionRequest {
  name?: string
  introText?: string
  contactPhone?: string
  address?: string
}

// ===== 员工 =====
export interface StaffListItem {
  id: number
  name: string
  phone: string
  role: Role
  isActive: boolean
  createdAt: string
}
export interface StaffCreatedResponse {
  id: number
  name: string
  phone: string
  role: Role
  isActive: boolean
}
export interface CreateStaffRequest { name: string; phone: string; role: Role }
export interface UpdateStaffRequest { name?: string; role?: Role }

// ===== 产品 =====
export interface ProductItem {
  id: number
  name: string
  type: ProductType
  price: number
  sessionCount: number
  description: string | null
  coverImagePath: string | null
  isPublished: boolean
  createdAt: string
}
export interface CreateProductRequest {
  name: string
  type: ProductType
  price: number
  sessionCount: number
  description?: string
}
export interface UpdateProductRequest {
  name?: string
  type?: ProductType
  price?: number
  sessionCount?: number
  description?: string
  coverImagePath?: string
}

// ===== 学员 =====
export interface StudentItem {
  id: number
  name: string
  phone: string
  gender: string | null
  birthday: string | null // DateOnly "yyyy-MM-dd"
  assignedSalesId: number | null
  assignedSalesName: string | null
  isActive: boolean
  createdAt: string
}
export interface StudentResponse {
  id: number
  name: string
  phone: string
  gender: string | null
  birthday: string | null
  assignedSalesId: number | null
  notes: string | null
  isActive: boolean
  createdAt: string
}
export interface StudentPackItem {
  packId: number // F5：walk-in 选包所需
  productId: number
  productName: string | null
  total: number
  remaining: number
  status: PackStatus
}
export interface StudentOrderItem {
  id: number
  orderNo: string
  productId: number
  productName: string | null
  amount: number
  status: OrderStatus
  createdAt: string
}
export interface StudentConsumptionItem {
  id: number
  kind: ConsumptionKind
  direction: number
  productName: string | null
  createdAt: string
}
export interface StudentEnrollmentItem {
  templateId: number
  dayOfWeek: string // 后端此 DTO 为枚举名字符串（与 Template 的 int 不同）
  startTime: string // TimeOnly "HH:mm:ss"
  endTime: string
  teacherName: string | null
  status: EnrollmentStatus
}
export interface StudentDetail {
  student: StudentResponse
  packs: StudentPackItem[]
  orders: StudentOrderItem[]
  recentConsumptions: StudentConsumptionItem[]
  enrollments: StudentEnrollmentItem[]
}
export interface CreateStudentRequest {
  name: string
  phone: string
  gender?: string
  birthday?: string
  assignedSalesId?: number
  notes?: string
}
export type UpdateStudentRequest = Partial<CreateStudentRequest>

// ===== 线索 / 公开页 =====
export interface IntentListItem {
  id: number
  name: string | null
  phone: string
  productId: number | null
  productName: string | null
  message: string | null
  status: IntentStatus
  followedByStaffId: number | null
  followedByName: string | null
  createdAt: string
}
export interface PublicOrgProductItem {
  id: number
  name: string
  type: ProductType
  price: number
  sessionCount: number
  description: string | null
  coverImagePath: string | null
}
export interface PublicOrgResponse {
  code: string
  name: string
  logoPath: string | null
  introText: string | null
  contactPhone: string | null
  address: string | null
  products: PublicOrgProductItem[]
}
export interface CreateIntentRequest {
  name?: string
  phone: string
  productId?: number
  message?: string
}
export interface IntentCreatedResponse { id: number }

// ===== 订单 =====
export interface OrderListItem {
  id: number
  orderNo: string
  studentId: number
  studentName: string
  productId: number
  productName: string
  amount: number
  salesId: number | null
  salesName: string | null
  status: OrderStatus
  createdAt: string
}
export interface OrderVoucherItem { id: number; url: string }
export interface OrderStudentSummary { id: number; name: string; phone: string }
export interface OrderProductSummary { id: number; name: string; type: ProductType }
export interface OrderPackSummary {
  id: number
  totalSessions: number
  remainingSessions: number
  status: PackStatus
}
export interface OrderDetail {
  id: number
  orderNo: string
  studentId: number
  productId: number
  amount: number
  salesId: number | null
  status: OrderStatus
  createdById: number
  confirmedById: number | null
  confirmedAt: string | null
  voidedById: number | null
  voidedAt: string | null
  voidReason: string | null
  refundAmount: number | null
  remark: string | null
  createdAt: string
  vouchers: OrderVoucherItem[]
  student: OrderStudentSummary
  product: OrderProductSummary
  pack: OrderPackSummary | null
}
export interface CreateOrderRequest {
  studentId: number
  productId: number
  amount: number
  salesId?: number
  remark?: string
}
export interface OrderCreatedResponse { id: number; orderNo: string; status: OrderStatus }
export interface VoidOrderRequest { reason: string; refundAmount?: number }

// ===== 文件 =====
export interface UploadFileResponse { path: string }
export interface VoucherCreatedResponse { id: number; url: string }

// ===== 班级模板 / 课表 =====
export interface TemplateListItem {
  id: number
  productId: number
  productName: string | null
  dayOfWeek: number // 0=周日
  startTime: string // "HH:mm:ss"
  endTime: string
  teacherId: number
  teacherName: string | null
  room: string | null
  capacity: number | null
  isActive: boolean
  enrolledCount: number
}
export interface TemplateResponse {
  id: number
  productId: number
  dayOfWeek: number
  startTime: string
  endTime: string
  teacherId: number
  room: string | null
  capacity: number | null
  isActive: boolean
}
export interface CreateTemplateRequest {
  productId: number
  dayOfWeek: number
  startTime: string // "HH:mm"
  endTime: string
  teacherId: number
  room?: string
  capacity?: number
}
export type UpdateTemplateRequest = Partial<CreateTemplateRequest>
export interface RosterItem {
  studentId: number
  name: string
  phone: string
  status: EnrollmentStatus
  enrolledAt: string
}
export interface EnrollmentCreatedResponse { id: number }
export interface SessionListItem {
  id: number
  templateId: number
  productId: number
  productName: string | null
  teacherId: number
  teacherName: string | null
  startAt: string
  endAt: string
  status: SessionStatus
  room: string | null
  enrolledCount: number
}
export interface SessionTemplateInfo {
  productId: number
  productName: string | null
  dayOfWeek: number
  startTime: string
  endTime: string
  room: string | null
  capacity: number | null
  isActive: boolean
}
export interface SessionRosterItem { studentId: number; name: string; phone: string }
export interface SessionAttendanceItem {
  studentId: number
  mark: Mark
  operatorId: number
  createdAt: string
}
export interface SessionDetail {
  id: number
  templateId: number
  teacherId: number
  teacherName: string | null
  startAt: string
  endAt: string
  status: SessionStatus
  template: SessionTemplateInfo | null
  roster: SessionRosterItem[]
  attendance: SessionAttendanceItem[]
}

// ===== 点名 =====
export interface MarkItemWire { studentId: number; mark: Mark }
export interface MarkResult { studentId: number; ok: boolean; error: string | null }
export interface AttendanceResult { results: MarkResult[] }

// ===== 核销流水 =====
export interface ConsumptionItem {
  id: number
  studentId: number
  studentName: string | null
  productName: string | null
  kind: ConsumptionKind
  direction: number
  reason: string | null
  operatorName: string | null
  classSessionStartAt: string | null
  createdAt: string
}
export interface WalkInResult { id: number; remainingSessions: number }
export interface ReverseResult { id: number }

// ===== 报表 =====
export interface SalesReportRow {
  staffId: number
  staffName: string | null
  orderCount: number
  totalAmount: number
}
export interface TeacherReportRow {
  teacherId: number
  teacherName: string | null
  completedSessions: number
  presentMarks: number
}

// ===== 机构页设置 =====
export interface OrgProfileResponse {
  code: string
  name: string
  logoPath: string | null
  introText: string | null
  contactPhone: string | null
  address: string | null
}
export interface UpdateOrgProfileRequest {
  name?: string
  logoPath?: string
  introText?: string
  contactPhone?: string
  address?: string
}

// ===== 家长端 =====
export interface ParentChildItem {
  studentId: number
  name: string
  institutionId: number
  institutionName: string | null
  institutionCode: string | null
}
export interface ParentPackItem {
  packId: number
  productName: string | null
  totalSessions: number
  remainingSessions: number
  status: PackStatus
  orderNo: string | null
  purchasedAt: string
}
export interface ParentRecordItem {
  id: number
  kind: ConsumptionKind
  direction: number
  productName: string | null
  createdAt: string
  reason: string | null
  classSessionStartAt: string | null
}
export interface ParentScheduleItem {
  startAt: string
  endAt: string
  productName: string | null
  teacherName: string | null
  room: string | null
  institutionName: string | null
}
