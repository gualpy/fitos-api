using FitOS.Application.Onboarding;
using FitOS.Infrastructure.Auth;
using FitOS.Migrator;

// Usage (run as the schema owner, never as fitos_app; connection string = argument 2 or FITOS_MIGRATE_CONNECTION):
//   migrate                                    apply pending SQL scripts
//   create-platform-admin <username> [name]    create a platform administrator
//                                              password: FITOS_ADMIN_PASSWORD, else a random one is printed once
var command = args.FirstOrDefault();
var connectionString = args.Length > 1 && command is "migrate" ? args[1] : Environment.GetEnvironmentVariable("FITOS_MIGRATE_CONNECTION");

if (command is not ("migrate" or "create-platform-admin") || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Usage: FitOS.Migrator migrate [connection] | create-platform-admin <username> [display name]");
    Console.Error.WriteLine("The connection string comes from FITOS_MIGRATE_CONNECTION (or the 2nd argument of migrate).");
    return 2;
}

if (command == "migrate")
{
    return MigrationRunner.Run(connectionString) ? 0 : 1;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Missing username.");
    return 2;
}

var username = args[1].Trim().ToLowerInvariant();
var displayName = args.Length > 2 ? args[2] : "Platform Admin";
var password = Environment.GetEnvironmentVariable("FITOS_ADMIN_PASSWORD");
var generated = string.IsNullOrEmpty(password);
password = generated ? TemporaryPassword.Generate() : password;

var created = await PlatformAdminBootstrap.CreateAsync(connectionString, username, displayName, password!, new IdentityPasswordService());
if (!created)
{
    Console.Error.WriteLine($"The user '{username}' already exists.");
    return 1;
}

Console.WriteLine($"Platform admin '{username}' created. It must change the password at first login.");
if (generated)
{
    Console.WriteLine($"Temporary password (shown once): {password}");
}

return 0;
