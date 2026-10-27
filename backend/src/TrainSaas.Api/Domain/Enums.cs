namespace TrainSaas.Api.Domain;

public enum UserType { Anonymous, Platform, Staff, Parent }

public enum InstitutionStatus { Active, Disabled }

public enum StaffRole { Admin, Sales, Teacher, FrontDesk }

public enum ProductType { Fixed, Flexible }

public enum OrderStatus { Pending, Active, Cancelled, Voided }

public enum PackStatus { Active, Voided }

public enum SessionStatus { Scheduled, Completed, Cancelled }

public enum EnrollmentStatus { Enrolled, Withdrawn }

public enum AttendanceMark { Present, Leave, Absent }

/// <summary>RollCall = 定时班点名扣课, WalkIn = 随到随上, Reversal = 冲正.</summary>
public enum ConsumptionKind { RollCall, WalkIn, Reversal }

public enum IntentStatus { New, Followed, Converted, Invalid }
