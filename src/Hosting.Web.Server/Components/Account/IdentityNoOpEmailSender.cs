using Norse.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Norse.Hosting.Web.Server.Components.Account;

// Remove the "else if (EmailSender is IdentityNoOpEmailSender)" block from RegisterConfirmation.razor after updating with a real implementation.
sealed class IdentityNoOpEmailSender : IEmailSender<NorseUser>
{
#pragma warning disable CA1859
	readonly IEmailSender _emailSender = new NoOpEmailSender();
#pragma warning restore CA1859

	/// <inheritdoc />
	public Task SendConfirmationLinkAsync(NorseUser user, string email, string confirmationLink) =>
		_emailSender.SendEmailAsync(email, "Confirm your email", $"Please confirm your account by <a href='{confirmationLink}'>clicking here</a>.");

	/// <inheritdoc />
	public Task SendPasswordResetLinkAsync(NorseUser user, string email, string resetLink) =>
		_emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password by <a href='{resetLink}'>clicking here</a>.");

	/// <inheritdoc />
	public Task SendPasswordResetCodeAsync(NorseUser user, string email, string resetCode) =>
		_emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password using the following code: {resetCode}");
}
