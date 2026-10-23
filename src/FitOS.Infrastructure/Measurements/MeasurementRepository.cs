using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Abstractions;
using FitOS.Application.Measurements;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Measurements;

public sealed class MeasurementRepository(FitOsDbContext db, ITenantContext tenant) : IMeasurementRepository
{
    public async Task<IReadOnlyList<BodyMeasurement>> ListAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<MeasurementRow>($"SELECT id, measured_on, weight_kg, height_cm FROM body_measurements WHERE customer_id = {customerId} ORDER BY measured_on")
            .ToListAsync(cancellationToken);
        return rows.Select(ToModel).ToList();
    }

    public async Task<BodyMeasurement> UpsertAsync(Guid customerId, DateOnly measuredOn, decimal weightKg, decimal heightCm, Guid? recordedBy, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.");
        var rows = await db.Database
            .SqlQuery<MeasurementRow>($"""
                INSERT INTO body_measurements (tenant_id, customer_id, measured_on, weight_kg, height_cm, recorded_by)
                VALUES ({tenantId}, {customerId}, {measuredOn}, {weightKg}, {heightCm}, {recordedBy})
                ON CONFLICT (tenant_id, customer_id, measured_on)
                DO UPDATE SET weight_kg = EXCLUDED.weight_kg, height_cm = EXCLUDED.height_cm, recorded_by = EXCLUDED.recorded_by
                RETURNING id, measured_on, weight_kg, height_cm
                """)
            .ToListAsync(cancellationToken);
        return ToModel(rows.Single());
    }

    public async Task<bool> DeleteAsync(Guid customerId, Guid id, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"DELETE FROM body_measurements WHERE id = {id} AND customer_id = {customerId}", cancellationToken) > 0;

    private static BodyMeasurement ToModel(MeasurementRow row) => new(row.Id, row.MeasuredOn, row.WeightKg, row.HeightCm);

    private sealed class MeasurementRow
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("measured_on")]
        public DateOnly MeasuredOn { get; set; }

        [Column("weight_kg")]
        public decimal WeightKg { get; set; }

        [Column("height_cm")]
        public decimal HeightCm { get; set; }
    }
}
