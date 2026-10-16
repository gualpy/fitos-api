using System.ComponentModel.DataAnnotations;

namespace FitOS.Infrastructure.Fiscal;

public sealed class SicofacOptions
{
    public const string Section = "Sicofac";

    /// <summary>Base URL of SICOFAC, e.g. http://sicofac:8000 inside the compose network.</summary>
    [Required, Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Timeout of one HTTP attempt.</summary>
    [Range(1, 120)]
    public int AttemptTimeoutSeconds { get; init; } = 15;
}
