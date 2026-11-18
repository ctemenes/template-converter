using System.Net;
using System.Text.Json.Nodes;
using Converter.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Converter.Functions;

public sealed class ConvertFunctions(ConversionPipeline pipeline)
{
    /// <summary>One template over HTTP. Body = the .docx bytes, header X-File-Name = its name.</summary>
    [Function("convert")]
    public async Task<HttpResponseData> Convert(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "convert")] HttpRequestData request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var name = request.Headers.TryGetValues("X-File-Name", out var values) ? values.First() : "uploaded.docx";

        var result = await pipeline.ConvertAsync(Path.GetFileName(name), buffer.ToArray());

        var response = request.CreateResponse(result.Status == "Unreadable" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(result.ToJson().ToJsonString());
        return response;
    }

    /// <summary>
    /// Batch path. Drop legacy templates into the "legacy" container. Each one is converted on its own,
    /// so a batch of thousands fans out across instances. The converted template and its report are
    /// written to blob storage, and anything a person has to look at goes on the "needs-review" queue.
    /// </summary>
    [Function("convert-on-upload")]
    public async Task<BatchOutput> ConvertOnUpload(
        [BlobTrigger("legacy/{name}", Connection = "AzureWebJobsStorage")] byte[] file, string name)
    {
        var result = await pipeline.ConvertAsync(name, file);
        var needsPerson = result.Status != "Converted";
        return new BatchOutput
        {
            Template = result.Template?.ToJsonString(),
            Report = result.ToJson(includeSource: false).ToJsonString(),
            Review = needsPerson
                ? new JsonObject
                {
                    ["file"] = name, ["status"] = result.Status, ["fidelity"] = result.Fidelity,
                    ["items"] = result.Validation?.NeedsReview ?? 0, ["error"] = result.Error
                }.ToJsonString()
                : null
        };
    }
}

public sealed class BatchOutput
{
    [BlobOutput("converted/{name}.template.json", Connection = "AzureWebJobsStorage")]
    public string? Template { get; set; }

    [BlobOutput("reports/{name}.report.json", Connection = "AzureWebJobsStorage")]
    public string? Report { get; set; }

    [QueueOutput("needs-review", Connection = "AzureWebJobsStorage")]
    public string? Review { get; set; }
}
