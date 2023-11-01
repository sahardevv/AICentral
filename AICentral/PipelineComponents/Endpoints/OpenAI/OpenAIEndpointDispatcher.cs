﻿using System.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AICentral.PipelineComponents.Endpoints.OpenAI;

public class OpenAIEndpointDispatcher : IAICentralEndpointDispatcher
{
    private readonly string _languageUrl;
    private readonly Dictionary<string, string> _modelMappings;
    private readonly IEndpointAuthorisationHandler _authHandler;

    public OpenAIEndpointDispatcher(
        string languageUrl,
        Dictionary<string, string> modelMappings,
        IEndpointAuthorisationHandler authHandler)
    {
        _languageUrl = languageUrl;
        _modelMappings = modelMappings;
        _authHandler = authHandler;
    }

    public async Task<(AICentralRequestInformation, HttpResponseMessage)> Handle(HttpContext context,
        AICentralPipelineExecutor pipeline, CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<OpenAIEndpointDispatcherBuilder>>();
        var typedDispatcher = context.RequestServices.GetRequiredService<HttpAIEndpointDispatcher>();

        context.Request.EnableBuffering(); //we may need to re-read the request if it fails.
        context.Request.Body.Position = 0;

        using var requestReader = new StreamReader(context.Request.Body, leaveOpen: true); //leave open in-case we need to re-read it. TODO, optimise this and read it once.
        var requestRawContent = await requestReader.ReadToEndAsync(cancellationToken);
        var deserializedRequestContent = (JObject)JsonConvert.DeserializeObject(requestRawContent)!;

        var extractor = new AzureOpenAiCallInformationExtractor();
        var callInformation = extractor.Extract(context.Request, deserializedRequestContent);

        var mappedModelName = _modelMappings.TryGetValue(callInformation.IncomingModelName, out var mapping)
            ? mapping
            : callInformation.IncomingModelName;

        var newUri = $"{_languageUrl}/openai/deployments/{mappedModelName}/{callInformation.RemainingUrl}";
        logger.LogDebug(
            "Rewritten URL from {OriginalUrl} to {NewUrl}. Incoming Model: {IncomingModelName}. Mapped Model: {MappedModelName}",
            context.Request.GetEncodedUrl(),
            newUri,
            callInformation.IncomingModelName,
            mappedModelName);

        var now = DateTimeOffset.Now;
        var sw = new Stopwatch();

        sw.Start();
        var openAiResponse =
            await typedDispatcher.Dispatch(context, newUri, requestRawContent, _authHandler, cancellationToken);

        //this will retry the operation for retryable status codes. When we reach here we might not want
        //to stream the response if it wasn't a 200.
        sw.Stop();

        //decision point... If this is a streaming request, then we should start streaming the result now.
        logger.LogDebug("Received Azure Open AI Response. Status Code: {StatusCode}", openAiResponse.StatusCode);

        var requestInformation =
            new AICentralRequestInformation(_languageUrl, callInformation.PromptText, now, sw.Elapsed);

        return (requestInformation, openAiResponse);
    }

    public object WriteDebug()
    {
        return new
        {
            Type = "AzureOpenAI",
            Url = _languageUrl,
            ModelMappings = _modelMappings,
            Auth = _authHandler.WriteDebug()
        };
    }

    public void ConfigureRoute(WebApplication app, IEndpointConventionBuilder route)
    {
    }
}