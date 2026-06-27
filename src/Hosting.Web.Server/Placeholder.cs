namespace Norse.Hosting.Web.Server;

static class Placeholder
{
	internal static string ServiceName(bool qualified = false) =>
		qualified ? "Norse.Hosting.Web.Server" : "Hosting.Web.Server";
}
