namespace CurveGuard;

public record PricingRiskCheck(string Code, int? BumpBp, double? Actual, double? Expected, double? Tolerance, bool Passed, string? Tenor = null, DateOnly? QuoteDate = null);

public record ValidationCheck(string Code, string Message, string? Tenor = null, DateOnly? QuoteDate = null, DateOnly? ExpectedDate = null, string? Contract = null);

public record ValidationResult(string Decision, string Result, string message, List<ValidationCheck> validationRisks, List<PricingRiskCheck> pricingRisks);
