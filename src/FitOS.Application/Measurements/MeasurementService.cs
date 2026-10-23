using FitOS.Application.Common;
using FitOS.Application.Customers;

namespace FitOS.Application.Measurements;

public sealed record BodyMeasurement(Guid Id, DateOnly MeasuredOn, decimal WeightKg, decimal HeightCm);

public sealed record MeasurementInput(DateOnly? MeasuredOn, decimal? WeightKg, decimal? HeightCm);

public interface IMeasurementRepository
{
    /// <summary>All of a customer's measurements, oldest first (the order a chart needs).</summary>
    Task<IReadOnlyList<BodyMeasurement>> ListAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Inserts the measurement, or replaces the one already recorded that day.</summary>
    Task<BodyMeasurement> UpsertAsync(Guid customerId, DateOnly measuredOn, decimal weightKg, decimal heightCm, Guid? recordedBy, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid customerId, Guid id, CancellationToken cancellationToken);
}

public sealed class MeasurementService(IMeasurementRepository measurements, ICustomerRepository customers, ITenantClock clock)
{
    public const decimal MinWeightKg = 20m;
    public const decimal MaxWeightKg = 400m;
    public const decimal MinHeightCm = 80m;
    public const decimal MaxHeightCm = 250m;

    public async Task<IReadOnlyList<BodyMeasurement>> ListAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        return await measurements.ListAsync(customerId, cancellationToken);
    }

    public async Task<BodyMeasurement> RecordAsync(Guid customerId, MeasurementInput input, Guid? recordedBy, CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        var today = await clock.TodayAsync(cancellationToken);
        var date = input.MeasuredOn ?? today;

        var errors = new ErrorBag();
        if (input.WeightKg is not { } weight)
        {
            errors.Add("weightKg", "required");
        }
        else if (weight is < MinWeightKg or > MaxWeightKg)
        {
            errors.Add("weightKg", "invalid");
        }

        if (input.HeightCm is not { } height)
        {
            errors.Add("heightCm", "required");
        }
        else if (height is < MinHeightCm or > MaxHeightCm)
        {
            errors.Add("heightCm", "invalid");
        }

        if (date > today)
        {
            errors.Add("measuredOn", "in_the_future");
        }

        errors.ThrowIfAny();

        return await measurements.UpsertAsync(
            customerId,
            date,
            Math.Round(input.WeightKg!.Value, 1, MidpointRounding.AwayFromZero),
            Math.Round(input.HeightCm!.Value, 1, MidpointRounding.AwayFromZero),
            recordedBy,
            cancellationToken);
    }

    public async Task DeleteAsync(Guid customerId, Guid id, CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        if (!await measurements.DeleteAsync(customerId, id, cancellationToken))
        {
            throw new NotFoundException("measurement");
        }
    }

    private async Task EnsureCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await customers.GetAsync(customerId, cancellationToken) is null)
        {
            throw new NotFoundException("customer");
        }
    }
}
