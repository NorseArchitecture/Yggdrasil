namespace Norse.Hosting.BrowserTesting;

static class BrowserTimeouts
{
	internal static readonly TimeSpan HostStartup = TimeSpan.FromSeconds(90);
	internal static readonly TimeSpan BrowserOperation = TimeSpan.FromSeconds(15);
	internal static readonly TimeSpan StoryState = TimeSpan.FromSeconds(15);
	internal static readonly TimeSpan Test = TimeSpan.FromMinutes(5);
	internal const float PlaywrightHostStartupMilliseconds = 90_000;
	internal const float PlaywrightOperationMilliseconds = 15_000;
	internal const float PlaywrightStoryStateMilliseconds = 15_000;
}
