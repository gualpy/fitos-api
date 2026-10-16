using System.Reflection;
using DbUp;

namespace FitOS.Migrator;

public static class MigrationRunner
{
    /// <summary>Applies pending embedded SQL scripts (Scripts/NNNN_name.sql) once each, in name order.</summary>
    public static bool Run(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .WithTransactionPerScript()
            .WithVariablesDisabled() // SQL uses $$ / $p$ dollar quoting, which DbUp would read as variables
            .LogToConsole()
            .Build();

        return upgrader.PerformUpgrade().Successful;
    }
}
