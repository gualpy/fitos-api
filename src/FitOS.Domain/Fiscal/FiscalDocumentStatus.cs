namespace FitOS.Domain.Fiscal;

/// <summary>Local state of a fiscal document (mirrors fiscal_documents.status).</summary>
public enum FiscalDocumentStatus
{
    Pending,
    Submitted,
    Authorized,
    Rejected,
    Failed,
    Voided,
}
