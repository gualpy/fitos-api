using System.Net.Mail;
using System.Text.RegularExpressions;
using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Domain.Customers;
using FitOS.Domain.Identification;

namespace FitOS.Application.Customers;

public sealed record CustomerInput(
    string? Name,
    string? IdentificationType,
    string? Identification,
    string? Email,
    string? Phone,
    string? Gender,
    DateOnly? BirthDate,
    string? RfidCard);

public sealed record CustomerSearch(string? Text, CustomerStatus? Status, int Page, int PageSize);

/// <param name="ActiveUntil">Last day of the membership covering today; null when not covered.</param>
public sealed record CustomerListItem(Customer Customer, DateOnly? ActiveUntil);

public interface ICustomerRepository
{
    Task<PagedResult<CustomerListItem>> SearchAsync(CustomerSearch search, DateOnly today, CancellationToken cancellationToken);

    Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Customer?> GetByClientCodeAsync(long clientCode, CancellationToken cancellationToken);

    Task<Customer?> GetByRfidCardAsync(string rfidCard, CancellationToken cancellationToken);

    void Add(Customer customer);

    /// <exception cref="ConflictException"><c>identification_taken</c> or <c>rfid_card_taken</c></exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <param name="checkIdentification">
/// When false (development only) cédula and RUC check digits are not verified, so test customers can use made-up numbers.
/// </param>
public sealed partial class CustomerService(ICustomerRepository customers, ITenantContext tenant, ITenantClock clock, bool checkIdentification = true)
{
    public const int MaxPageSize = 100;

    public async Task<PagedResult<CustomerListItem>> SearchAsync(string? text, CustomerStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var search = new CustomerSearch(
            string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            status,
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, MaxPageSize));
        return await customers.SearchAsync(search, await clock.TodayAsync(cancellationToken), cancellationToken);
    }

    public async Task<Customer> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await customers.GetAsync(id, cancellationToken) ?? throw new NotFoundException("customer");

    public async Task<Customer> CreateAsync(CustomerInput input, CancellationToken cancellationToken)
    {
        var today = await clock.TodayAsync(cancellationToken);
        var customer = new Customer
        {
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
            JoinedAt = today,
        };
        Apply(customer, input, today);

        customers.Add(customer);
        await customers.SaveAsync(cancellationToken);
        return customer;
    }

    public async Task<Customer> UpdateAsync(Guid id, CustomerInput input, CancellationToken cancellationToken)
    {
        var customer = await GetAsync(id, cancellationToken);
        Apply(customer, input, await clock.TodayAsync(cancellationToken));

        await customers.SaveAsync(cancellationToken);
        return customer;
    }

    public async Task<Customer> SetStatusAsync(Guid id, CustomerStatus status, CancellationToken cancellationToken)
    {
        var customer = await GetAsync(id, cancellationToken);
        customer.Status = status;
        await customers.SaveAsync(cancellationToken);
        return customer;
    }

    private void Apply(Customer customer, CustomerInput input, DateOnly today)
    {
        var errors = new ErrorBag();

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors.Add("name", "required");
        }
        else if (name.Length > 120)
        {
            errors.Add("name", "too_long");
        }

        var idType = string.IsNullOrWhiteSpace(input.IdentificationType) ? null : input.IdentificationType.Trim();
        var id = string.IsNullOrWhiteSpace(input.Identification) ? null : input.Identification.Trim();
        if ((idType is null) != (id is null))
        {
            errors.Add(idType is null ? "identificationType" : "identification", "required_together");
        }
        else if (idType is not null && id is not null)
        {
            ValidateIdentification(errors, idType, id);
        }

        var email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        if (email is not null && !IsEmail(email))
        {
            errors.Add("email", "invalid");
        }

        var phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        if (phone is not null && !PhonePattern().IsMatch(phone))
        {
            errors.Add("phone", "invalid");
        }

        var gender = string.IsNullOrWhiteSpace(input.Gender) ? null : input.Gender.Trim().ToUpperInvariant();
        if (gender is not null and not ("M" or "F" or "O"))
        {
            errors.Add("gender", "invalid");
        }

        if (input.BirthDate is { } birth && (birth > today || birth < today.AddYears(-120)))
        {
            errors.Add("birthDate", "invalid");
        }

        var rfid = string.IsNullOrWhiteSpace(input.RfidCard) ? null : input.RfidCard.Trim();
        if (rfid is { Length: > 50 })
        {
            errors.Add("rfidCard", "too_long");
        }

        errors.ThrowIfAny();

        customer.Name = name!;
        customer.IdentificationType = idType;
        customer.Identification = id;
        customer.Email = email;
        customer.Phone = phone;
        customer.Gender = gender;
        customer.BirthDate = input.BirthDate;
        customer.RfidCard = rfid;
    }

    private void ValidateIdentification(ErrorBag errors, string type, string id)
    {
        var valid = type switch
        {
            "05" => checkIdentification ? EcuadorId.IsValidCedula(id) : IsLooseId(id),
            "04" => checkIdentification ? EcuadorId.IsValidRuc(id) : IsLooseId(id),
            "06" or "08" => IsLooseId(id),
            _ => false,
        };

        if (!valid)
        {
            errors.Add(type is "04" or "05" or "06" or "08" ? "identification" : "identificationType", "invalid");
        }
    }

    private static bool IsLooseId(string id) => id.Length is >= 3 and <= 20 && id.All(char.IsAsciiLetterOrDigit);

    private static bool IsEmail(string value) =>
        value.Length <= 254 && MailAddress.TryCreate(value, out var address) && address.Address == value;

    [GeneratedRegex(@"^\+?[0-9 ()-]{7,30}$")]
    private static partial Regex PhonePattern();
}
