using FitOS.Application.Auth;
using FitOS.Application.Onboarding;
using FitOS.Infrastructure.Auth;
using FitOS.Migrator;

// Usage (run as the schema owner, never as fitos_app; connection string = argument 2 or FITOS_MIGRATE_CONNECTION):
//   migrate                                    apply pending SQL scripts
//   create-platform-admin <username> [name]    create a platform administrator
//                                              password: FITOS_ADMIN_PASSWORD, else a random one is printed once
//                                              FITOS_ADMIN_EMAIL (optional): where the login confirmation link is sent
//   set-platform-email <username> <email>      set or change the e-mail of a platform administrator
//   seed-dev <gym-slug> <username> <name> [days]  DEV ONLY: a customer with a login, a membership and attendance on every open day
//                                              needs FITOS_ALLOW_DEV_SEED=1 and FITOS_SEED_PASSWORD (min. 10 chars, letter + non-letter)
var command = args.FirstOrDefault();
var connectionString = args.Length > 1 && command is "migrate" ? args[1] : Environment.GetEnvironmentVariable("FITOS_MIGRATE_CONNECTION");

if (command is not ("migrate" or "create-platform-admin" or "set-platform-email" or "seed-dev") || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Usage: FitOS.Migrator migrate [connection] | create-platform-admin <username> [display name] | set-platform-email <username> <email> | seed-dev <gym-slug> <username> <name> [days]");
    Console.Error.WriteLine("The connection string comes from FITOS_MIGRATE_CONNECTION (or the 2nd argument of migrate).");
    return 2;
}

if (command == "migrate")
{
    return MigrationRunner.Run(connectionString) ? 0 : 1;
}

if (command == "set-platform-email")
{
    var address = args.Length > 2 ? args[2].Trim() : string.Empty;
    if (args.Length < 3 || !System.Text.RegularExpressions.Regex.IsMatch(address, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
    {
        Console.Error.WriteLine("Usage: set-platform-email <username> <email>");
        return 2;
    }

    if (!await PlatformAdminBootstrap.SetEmailAsync(connectionString, args[1].Trim().ToLowerInvariant(), address))
    {
        Console.Error.WriteLine($"There is no platform administrator '{args[1]}'.");
        return 1;
    }

    Console.WriteLine($"E-mail of '{args[1]}' updated.");
    return 0;
}

if (command == "seed-dev")
{
    // Seeded users have a known password: never let this touch a real database by accident.
    if (Environment.GetEnvironmentVariable("FITOS_ALLOW_DEV_SEED") != "1")
    {
        Console.Error.WriteLine("seed-dev creates users with a password you choose. Set FITOS_ALLOW_DEV_SEED=1 to confirm this is a development database.");
        return 2;
    }

    var seedPassword = Environment.GetEnvironmentVariable("FITOS_SEED_PASSWORD") ?? string.Empty;
    if (args.Length < 4 || !ChangePasswordService.IsAcceptable(seedPassword))
    {
        Console.Error.WriteLine("Usage: seed-dev <gym-slug> <username> <display name> [days]; FITOS_SEED_PASSWORD must be 10+ chars with a letter and a non-letter.");
        return 2;
    }

    var days = args.Length > 4 && int.TryParse(args[4], out var parsedDays) ? parsedDays : 60;
    try
    {
        var seeded = await DevSeeder.SeedAsync(
            connectionString,
            new SeedOptions(args[1].Trim().ToLowerInvariant(), args[2].Trim().ToLowerInvariant(), args[3], seedPassword, days),
            new IdentityPasswordService(),
            TimeProvider.System);
        Console.WriteLine($"Seed done for '{args[2]}': customer created = {seeded.CustomerCreated}, memberships added = {seeded.MembershipsCreated}, attendance days added = {seeded.AttendanceCreated}.");
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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

var adminEmail = Environment.GetEnvironmentVariable("FITOS_ADMIN_EMAIL");
var created = await PlatformAdminBootstrap.CreateAsync(
    connectionString, username, displayName, password!, new IdentityPasswordService(), string.IsNullOrWhiteSpace(adminEmail) ? null : adminEmail.Trim());
if (!created)
{
    Console.Error.WriteLine($"The user '{username}' already exists.");
    return 1;
}

Console.WriteLine($"Platform admin '{username}' created. It must change the password at first login.");
if (string.IsNullOrWhiteSpace(adminEmail))
{
    Console.WriteLine("No e-mail given: the login confirmation link needs one. Run: set-platform-email <username> <email>");
}
if (generated)
{
    Console.WriteLine($"Temporary password (shown once): {password}");
}

return 0;
