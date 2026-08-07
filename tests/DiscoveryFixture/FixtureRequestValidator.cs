using FluentValidation;

namespace Norse.DiscoveryFixture;

/// <summary>
/// The one validator this fixture carries — proves the generated client-side discovery finds and
/// registers a validator declared in a referenced RCL, not just one declared in a host project
/// itself.
/// </summary>
public sealed class FixtureRequestValidator : AbstractValidator<FixtureRequest>
{
	public FixtureRequestValidator() =>
		RuleFor(request => request.Name).NotEmpty();
}
