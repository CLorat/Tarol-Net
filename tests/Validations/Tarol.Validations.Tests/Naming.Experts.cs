namespace Tarol.Validations.Tests.Experts;

public readonly partial record struct Name([Length(3, 120)] string Value);
