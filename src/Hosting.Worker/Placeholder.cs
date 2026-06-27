namespace Norse.Hosting.Worker;

static class Placeholder
{
	internal static string ServiceName(bool qualified = false) =>
		qualified ? "Norse.Hosting.Worker" : "Hosting.Worker";
}
