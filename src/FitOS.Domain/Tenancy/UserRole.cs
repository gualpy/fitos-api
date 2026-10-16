namespace FitOS.Domain.Tenancy;

public enum UserRole
{
    PlatformAdmin,
    Owner,
    Admin,
    Reception,
    Customer,
}

public static class UserRoleWire
{
    public static string ToWire(this UserRole role) => role switch
    {
        UserRole.PlatformAdmin => "platform_admin",
        UserRole.Owner => "owner",
        UserRole.Admin => "admin",
        UserRole.Reception => "reception",
        UserRole.Customer => "customer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static UserRole Parse(string wire) => wire switch
    {
        "platform_admin" => UserRole.PlatformAdmin,
        "owner" => UserRole.Owner,
        "admin" => UserRole.Admin,
        "reception" => UserRole.Reception,
        "customer" => UserRole.Customer,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, "Unknown role."),
    };
}
