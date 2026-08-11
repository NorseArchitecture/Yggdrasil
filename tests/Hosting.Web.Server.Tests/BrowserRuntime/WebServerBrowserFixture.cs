using System.Linq.Expressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Norse.Abstractions.Backend;
using Norse.Abstractions.Contracts;
using Norse.Hosting.BrowserTesting;
using Norse.Primitives.Identifiers;
using Norse.Reference;
using Norse.Reference.Data.EntityFramework;

namespace Norse.Hosting.Web.Server.Tests.BrowserRuntime;

public sealed class WebServerBrowserFixture : IAsyncLifetime
{
	readonly WebServerBrowserHostFixture _host = new();

	internal Uri Origin => _host.Origin;

	public ValueTask InitializeAsync() => _host.InitializeAsync();

	internal Task<BrowserEvidence> OpenEvidenceAsync(string testName) =>
		_host.OpenEvidenceAsync(testName);

	internal static BrowserEvidencePolicy CreateEvidencePolicy() => new WebServerBrowserEvidencePolicy();

	public ValueTask DisposeAsync() => _host.DisposeAsync();
}

sealed class WebServerBrowserHostFixture : BrowserHostFixture<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder) =>
		builder.ConfigureTestServices(services =>
		{
			var row = Iso3166.All.Single(static row => row.Code is IsoCountryCode.UnitedStatesOfAmerica);
			DeterministicGuid id = new(Iso3166.Ids[IsoCountryCode.UnitedStatesOfAmerica]);
			CountryOrAreaView view = new()
			{
				Id = id,
				Code = row.Code,
				Alpha2 = row.Alpha2,
				Alpha3 = row.Alpha3,
				Name = row.Name,
				Classification = Classification.None,
			};

			var repository = Substitute.For<IReadRepository<CountryOrAreaView>>();
			repository
				.GetAsync(
					Arg.Any<Guid>(),
					Arg.Any<Expression<Func<CountryOrAreaView, CountryResponse>>>(),
					Arg.Any<CancellationToken>())
				.Returns(Outcome<CountryResponse>.Err(ErrorCategory.NotFound));
			repository
				.GetAsync(
					(Guid)id,
					Arg.Any<Expression<Func<CountryOrAreaView, CountryResponse>>>(),
					Arg.Any<CancellationToken>())
				.Returns(call => Task.FromResult(Outcome<CountryResponse>.Ok(
					call.Arg<Expression<Func<CountryOrAreaView, CountryResponse>>>().Compile()(view))));

			services.RemoveAll<IReadRepository<CountryOrAreaView>>();
			services.AddSingleton(repository);
		});
}

sealed class WebServerBrowserEvidencePolicy : BrowserEvidencePolicy
{
	const string OverflowMessage =
		"Error: The custom event 'overflowchange' cannot have the same name as its browserEventName 'overflowchange'. Choose a different name for the custom event.";
	const string AccordionMessage = "Error: The event 'accordionchange' is already registered.";
	const string FluentUiModulePath =
		"/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:";
	int _acceptedOverflowErrors;
	int _acceptedAccordionErrors;
	int _acceptedDisconnectFailures;

	internal WebServerBrowserEvidencePolicy() :
		base("Web.Server Fluent UI rc5 InteractiveAuto startup")
	{
	}

	internal override bool IsExpectedPageError(string error)
	{
		// rc5 registers overflowchange under the same custom and browser names. Blazor rejects it
		// before rc5 sets its one-time startup flag, so the next Server/WASM initializer retries at
		// accordionchange and reports that already-completed registration. This page uses neither
		// Accordion nor Overflow; the bounded signatures below are only for those two startup faults.
		if (MatchesFluentUiRegistrationError(error, OverflowMessage, "Overflow"))
		{
			if (_acceptedOverflowErrors >= 2)
				return false;
			_acceptedOverflowErrors++;
			return true;
		}

		if (!MatchesFluentUiRegistrationError(error, AccordionMessage, "Accordion"))
			return false;
		if (_acceptedAccordionErrors >= 2)
			return false;
		_acceptedAccordionErrors++;
		return true;
	}

	internal override bool IsExpectedRequestFailure(IRequest request)
	{
		if (_acceptedDisconnectFailures >= 1 ||
			request.Method != "POST" ||
			request.Failure != "net::ERR_ABORTED" ||
			!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri) ||
			requestUri.AbsolutePath != "/_blazor/disconnect")
			return false;

		_acceptedDisconnectFailures++;
		return true;
	}

	static bool MatchesFluentUiRegistrationError(string error, string message, string eventAlias)
	{
		var lines = error.Split('\n');
		return lines.Length >= 3 &&
			lines[0].StartsWith("page-error page=http://127.0.0.1:", StringComparison.Ordinal) &&
			lines[0].EndsWith($": {message}", StringComparison.Ordinal) &&
			lines[1].StartsWith(
				"    at Object.registerCustomEventType (http://127.0.0.1:",
				StringComparison.Ordinal) &&
			lines[1].Contains("/_framework/blazor.web.", StringComparison.Ordinal) &&
			lines[2].StartsWith("    at Object.", StringComparison.Ordinal) &&
			lines[2].Contains(
				$" [as {eventAlias}] (http://127.0.0.1:",
				StringComparison.Ordinal) &&
			lines[2].Contains(FluentUiModulePath, StringComparison.Ordinal);
	}
}
