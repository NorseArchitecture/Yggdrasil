using Microsoft.AspNetCore.Identity;
using Norse.Hosting.Web.Server.Components.Account;
using Norse.Identity;

namespace Norse.Hosting.Web.Server.Tests;

public class IdentityNoOpEmailSenderTests
{
	readonly IEmailSender<NorseUser> _sender = new IdentityNoOpEmailSender();
	readonly NorseUser _user = new();

	[Fact]
	async Task SendConfirmationLinkAsync_completes_without_throwing() =>
		await _sender.SendConfirmationLinkAsync(_user, "user@example.com", "https://example.com/confirm");

	[Fact]
	async Task SendPasswordResetLinkAsync_completes_without_throwing() =>
		await _sender.SendPasswordResetLinkAsync(_user, "user@example.com", "https://example.com/reset");

	[Fact]
	async Task SendPasswordResetCodeAsync_completes_without_throwing() =>
		await _sender.SendPasswordResetCodeAsync(_user, "user@example.com", "123456");
}
