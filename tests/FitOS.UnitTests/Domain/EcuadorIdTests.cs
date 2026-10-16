using FitOS.Domain.Identification;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Domain;

public sealed class EcuadorIdTests
{
    [Theory]
    [InlineData("1710034065")]
    public void Valid_cedula_is_accepted(string cedula) => Assert.True(EcuadorId.IsValidCedula(cedula));

    [Theory]
    [InlineData("1710034066")] // wrong check digit
    [InlineData("9910034065")] // no such province
    [InlineData("1760034065")] // 3rd digit 6 is not a person
    [InlineData("171003406")] // too short
    [InlineData("17100340650")] // too long
    [InlineData("17100X4065")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_cedula_is_rejected(string? cedula) => Assert.False(EcuadorId.IsValidCedula(cedula));

    [Theory]
    [InlineData("1710034065001")] // natural person: cédula + establishment
    [InlineData("1790016919001")] // private company (3rd digit 9, modulo 11)
    [InlineData("1760001550001")] // public entity (3rd digit 6)
    public void Valid_ruc_is_accepted(string ruc) => Assert.True(EcuadorId.IsValidRuc(ruc));

    [Theory]
    [InlineData("1710034066001")] // person: bad check digit
    [InlineData("1790016918001")] // company: bad check digit
    [InlineData("1760001560001")] // public: bad check digit (9th digit)
    [InlineData("1710034065000")] // establishment 000
    [InlineData("1790016919000")]
    [InlineData("1760001550000")]
    [InlineData("1770016919001")] // 3rd digit 7 is not a RUC type
    [InlineData("1710034065")] // a cédula is not a RUC
    [InlineData("99100340650011")]
    [InlineData("abcdefghijklm")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_ruc_is_rejected(string? ruc) => Assert.False(EcuadorId.IsValidRuc(ruc));
}

public sealed class TenantSlugTests
{
    [Theory]
    [InlineData("titan")]
    [InlineData("gym-2")]
    [InlineData("0fit")]
    public void Normal_slugs_are_available(string slug) => Assert.True(TenantSlug.IsAvailableForTenant(slug));

    [Theory]
    [InlineData("www")]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("platform")]
    public void Reserved_slugs_are_well_formed_but_not_available(string slug)
    {
        Assert.True(TenantSlug.IsWellFormed(slug));
        Assert.False(TenantSlug.IsAvailableForTenant(slug));
    }

    [Theory]
    [InlineData("Titan")]
    [InlineData("-titan")]
    [InlineData("ti tan")]
    [InlineData("ti.tan")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_slugs_are_rejected(string? slug) => Assert.False(TenantSlug.IsWellFormed(slug));

    [Fact]
    public void Slug_longer_than_a_dns_label_is_rejected() => Assert.False(TenantSlug.IsWellFormed(new string('a', 64)));
}
