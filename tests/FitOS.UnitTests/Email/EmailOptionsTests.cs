using FitOS.Infrastructure.Email;

namespace FitOS.UnitTests.Email;

public sealed class EmailOptionsTests
{
    [Fact]
    public void CertificateRevocationCheck_IsOnByDefault()
    {
        // Production keeps the CRL/OCSP check; only a local dev server may turn it off via configuration.
        Assert.True(new EmailOptions().CheckCertificateRevocation);
    }
}
