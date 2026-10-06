namespace Inventory.Shared.Security;

public static class Roles
{
    public const string Admin = "admin";
    public const string Teacher = "teacher";
    public const string Student = "student";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Admin, Teacher, Student };

    /// <summary>Роли, которые администратор может назначать через API (SR-05).</summary>
    public static readonly IReadOnlySet<string> Assignable = new HashSet<string> { Teacher, Student };
}

/// <summary>
/// Имена политик авторизации. Каждая защищённая операция явно указывает, какой
/// политике она подчиняется (D-01). Если политика забыта, срабатывает fallback-политика,
/// требующая аутентификации, поэтому операция не становится публичной случайно.
/// </summary>
public static class Policies
{
    public const string Admin = "role:admin";
    public const string Teacher = "role:teacher";
    public const string Student = "role:student";
    public const string AdminOrTeacher = "role:admin-or-teacher";
    public const string TeacherOrStudent = "role:teacher-or-student";
    public const string AnyRole = "role:any";
}

/// <summary>Имена claim'ов в JWT, выпускаемом сервисом аутентификации.</summary>
public static class InventoryClaims
{
    public const string UserId = "sub";
    public const string Login = "login";
    public const string FullName = "name";
    public const string Role = "role";
    public const string TokenId = "jti";
}
