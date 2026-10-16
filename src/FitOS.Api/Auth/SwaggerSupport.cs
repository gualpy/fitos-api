using FitOS.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FitOS.Api.Auth;

/// <summary>Development convenience: lets Swagger UI send the JWT and the tenant header of the login call.</summary>
public static class SwaggerSupport
{
    public static void Configure(SwaggerGenOptions options)
    {
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste the accessToken returned by /auth/login.",
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
        });
        options.OperationFilter<TenantHeaderFilter>();
    }

    private sealed class TenantHeaderFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (context.ApiDescription.RelativePath != "auth/login")
            {
                return;
            }

            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = TenantHostResolver.TenantHeader,
                In = ParameterLocation.Header,
                Required = true,
                Description = "Development only. Gym slug, or 'platform' for platform administrators.",
                Schema = new OpenApiSchema { Type = "string" },
            });
        }
    }
}
