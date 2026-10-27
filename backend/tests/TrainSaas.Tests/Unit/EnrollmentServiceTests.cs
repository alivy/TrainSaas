using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// EnrollmentService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）。
/// 本文件聚焦数据库兜底：报名行复用语义（同一 TemplateId+StudentId 至多
/// 一行）由唯一索引硬性保证——绕过服务直插第二行必须被拒。
/// </summary>
public class EnrollmentServiceTests
{
    /// <summary>当前用户：机构 1 的管理员（StaffId=1）。</summary>
    private readonly FakeCurrentUser _user = new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = 1,
        Role = StaffRole.Admin,
        Phone = "13900000001",
        Name = "管理员",
    };

    /// <summary>
    /// 数据库兜底：即使绕过服务直插两行同 (TemplateId,StudentId) 的报名，
    /// 唯一索引也拒绝（DbUpdateException）。并发双报时输家的插入由此变成
    /// DbUpdateException，由 EnrollAsync 捕获重读后收敛为 409，而非静默
    /// 双行（与 Task 10/11 的唯一索引兜底模式一致）。
    /// </summary>
    [Fact]
    public async Task ClassEnrollment_template_and_student_unique_index_rejects_duplicate()
    {
        var (db, conn) = TestDb.Create(_user);
        var teacher = new Staff
        {
            InstitutionId = 1,
            Name = "教师甲",
            Phone = "13911110001",
            PasswordHash = "not-a-real-hash",
            Role = StaffRole.Teacher,
            IsActive = true,
        };
        db.Staff.Add(teacher);
        var template = new ClassTemplate
        {
            InstitutionId = 1,
            ProductId = 1,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            TeacherId = teacher.Id,
            IsActive = true,
        };
        db.ClassTemplates.Add(template);
        var student = new Student
        {
            InstitutionId = 1,
            Name = "学员甲",
            Phone = "13811110001",
            IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.ClassEnrollments.Add(new ClassEnrollment
        {
            TemplateId = template.Id,
            StudentId = student.Id,
            EnrolledById = 1,
            Status = EnrollmentStatus.Enrolled,
        });
        db.ClassEnrollments.Add(new ClassEnrollment
        {
            TemplateId = template.Id,
            StudentId = student.Id,
            EnrolledById = 1,
            Status = EnrollmentStatus.Enrolled,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
