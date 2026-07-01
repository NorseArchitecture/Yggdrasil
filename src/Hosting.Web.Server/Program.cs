using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.FluentUI.AspNetCore.Components;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Server.Components;
using Norse.Hosting.Web.Server.Components.Account;
using Norse.Hosting.Web.Server.Identity;

Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents()
	.AddInteractiveWebAssemblyComponents();

builder.Services.AddFluentUIComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services
	.AddAuthentication(options =>
	{
		options.DefaultScheme = IdentityConstants.ApplicationScheme;
		options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
	})
	.AddIdentityCookies();

// NOTE: no .AddEntityFrameworkStores<T>() here — persistence lands in a separate tree.
// PlaceholderUserStore satisfies DI validation; anything that actually touches it throws
// until that tree replaces it with a real EF-backed IUserStore.
builder.Services.AddScoped<IUserStore<ApplicationUser>, PlaceholderUserStore>();
builder.Services
	.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
	.AddSignInManager()
	.AddDefaultTokenProviders();

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}
else
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode()
	.AddInteractiveWebAssemblyRenderMode()
	.AddAdditionalAssemblies(typeof(Routes).Assembly);

app.MapAdditionalIdentityEndpoints();

await app
	.RunAsync()
	.ConfigureAwait(false);
