using System.Net.Mime;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Norse.Hosting.Web.Server.OpenApi;

/// <summary>
///     Stamps the standard idiomatic response codes onto every operation in the document, regardless of
///     the controller/operation combo — the codes a caller can hit on any Norse REST route whether or not
///     the action's own signature mentions them: the facade fold (400/404 from
///     <c>GrpcControllerBase.FoldAsync</c>, 401/403 from the authorization behavior), content negotiation
///     (406 via <c>ReturnHttpNotAcceptable</c>), and the infrastructure in front of the host
///     (429/500/502/503/504). Insertion order is deliberate — responses render in the order they were
///     added. A 400 is stamped only where untrusted input exists to reject (a request body or bound
///     parameters — on this platform a route-bound code is a parse event, so parameters count); a 404 only
///     where a parameter identifies a resource to miss. An operation that already declares one of these
///     codes keeps its own — <c>TryAdd</c>, never overwrite. The 400 carries the platform's actual failure
///     body: RFC 9457 problem details plus the house <c>errors</c> array (<c>[{path, detail}]</c> — the
///     one shape both MVC model binding and a failed <c>Outcome&lt;T&gt;</c> render), negotiated as
///     <c>application/problem+json</c>/<c>application/problem+xml</c>, referencing the <c>Problem</c>
///     component schema this transformer registers into the document once.
/// </summary>
sealed class StandardResponsesTransformer : IOpenApiOperationTransformer
{
	const string ProblemSchemaId = "Problem";

	/// <inheritdoc />
	public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
		CancellationToken cancellationToken)
	{
		var document = context.Document
			?? throw new InvalidOperationException("operation transformer ran with no document in context");
		EnsureProblemComponent(document);

		operation.Responses ??= [];
		NormalizeSuccessContent(operation.Responses);
		if (operation.RequestBody is not null || operation.Parameters is { Count: > 0 })
		{
			OpenApiMediaType problemMedia = new()
			{
				Schema = new OpenApiSchemaReference(ProblemSchemaId, document)
			};
			operation.Responses.TryAdd("400", new OpenApiResponse
			{
				Description = "Bad Request",
				Content = new Dictionary<string, IOpenApiMediaType>
				{
					[MediaTypeNames.Application.ProblemJson] = problemMedia,
					[MediaTypeNames.Application.ProblemXml] = problemMedia
				}
			});
		}

		operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
		operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Forbidden" });
		if (operation.Parameters is { Count: > 0 })
			operation.Responses.TryAdd("404", new OpenApiResponse { Description = "Not Found" });
		operation.Responses.TryAdd("406", new OpenApiResponse { Description = "Not Acceptable" });
		operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Too Many Requests" });
		operation.Responses.TryAdd("500", new OpenApiResponse { Description = "Internal Server Error" });
		operation.Responses.TryAdd("502", new OpenApiResponse { Description = "Bad Gateway" });
		operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Service Unavailable" });
		operation.Responses.TryAdd("504", new OpenApiResponse { Description = "Gateway Timeout" });
		return Task.CompletedTask;
	}

	/// <summary>
	///     With the Swashbuckle-era <c>[Produces]</c> pair gone, ApiExplorer unions every registered
	///     output formatter's media types into a success response's content — including
	///     <c>text/plain</c> (which the endpoint honestly 406s for a contract payload, since
	///     <c>StringOutputFormatter</c> cannot write one) and the <c>text/json</c>/<c>text/xml</c>
	///     legacy aliases. A document that leads with a media type the wire refuses sends every
	///     first-pick client (Scalar's try-it defaults to the first entry) straight into that 406.
	///     Rewritten to the platform's two real channels, JSON first — the same declaration the old
	///     attribute made, now derived where the document is authored instead of stamped per
	///     controller.
	/// </summary>
	static void NormalizeSuccessContent(OpenApiResponses responses)
	{
		foreach (var (statusCode, response) in responses)
		{
			if (statusCode is not ['2', ..] ||
				response is not OpenApiResponse { Content: { Count: > 0 } content })
				continue;

			var media = content.Values.First();
			content.Clear();
			content[MediaTypeNames.Application.Json] = media;
			content[MediaTypeNames.Application.Xml] = media;
		}
	}

	static void EnsureProblemComponent(OpenApiDocument document)
	{
		document.Components ??= new OpenApiComponents();
		document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
		if (document.Components.Schemas.ContainsKey(ProblemSchemaId))
			return;

		document.Components.Schemas[ProblemSchemaId] = new OpenApiSchema
		{
			Type = JsonSchemaType.Object,
			Description =
				"RFC 9457 problem details with the platform's errors array — the one failure shape every "
				+ "Norse 400 renders, whether the rejection came from MVC model binding or a failed Outcome.",
			Properties = new Dictionary<string, IOpenApiSchema>
			{
				["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
				["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
				["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
				["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
				["instance"] = new OpenApiSchema { Type = JsonSchemaType.String },
				["errors"] = new OpenApiSchema
				{
					Type = JsonSchemaType.Array,
					Items = new OpenApiSchema
					{
						Type = JsonSchemaType.Object,
						Properties = new Dictionary<string, IOpenApiSchema>
						{
							["path"] = new OpenApiSchema { Type = JsonSchemaType.String },
							["detail"] = new OpenApiSchema { Type = JsonSchemaType.String }
						}
					}
				}
			}
		};
	}
}
