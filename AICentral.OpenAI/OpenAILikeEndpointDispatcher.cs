﻿using AICentral.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AICentral.OpenAI;

public abstract class OpenAILikeEndpointDispatcher : IEndpointRequestResponseHandler
{
    private readonly Dictionary<string, string> _modelMappings;
    private static readonly HashSet<string> HeadersToIgnore = new(new[] { "host", "authorization", "api-key" });

    protected OpenAILikeEndpointDispatcher(
        string id,
        string baseUrl,
        string endpointName,
        Dictionary<string, string> modelMappings)
    {
        EndpointName = endpointName;
        Id = id;
        BaseUrl = baseUrl;
        _modelMappings = modelMappings;
    }

    /// <summary>
    /// Opportunity to pull specific diagnostics and, for example, raise your own telemetry events.
    /// </summary>
    /// <param name="incomingCallDetails"></param>
    /// <param name="downstreamRequest"></param>
    /// <param name="openAiResponse"></param>
    /// <returns></returns>
    protected abstract Task ExtractDiagnostics(
        IncomingCallDetails incomingCallDetails,
        HttpRequestMessage downstreamRequest,
        HttpResponseMessage openAiResponse);

    public bool IsAffinityRequestToMe(string affinityHeaderValue)
    {
        return EndpointName == affinityHeaderValue;
    }

    private static bool MappedModelFoundAsEmptyString(AICallInformation callInformation, string mappedModelName)
    {
        return callInformation.IncomingCallDetails.AICallType != AICallType.Other && mappedModelName == string.Empty;
    }

    private async Task<HttpRequestMessage> BuildNewRequest(HttpContext context, AICallInformation callInformation,
        string? mappedModelName)
    {
        var newRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), BuildUri(context, callInformation, mappedModelName));
        
        foreach (var header in context.Request.Headers)
        {
            if (HeadersToIgnore.Contains(header.Key.ToLowerInvariant())) continue;

            if (!newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) &&
                newRequest.Content != null)
            {
                newRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        await CustomiseRequest(context, callInformation, newRequest, mappedModelName);

        return newRequest;
    }

    protected abstract Task CustomiseRequest(HttpContext context, AICallInformation callInformation,
        HttpRequestMessage newRequest, string? newModelName);

    protected abstract string BuildUri(HttpContext context, AICallInformation aiCallInformation,
        string? mappedModelName);

    public string Id { get; }
    public string BaseUrl { get; }
    public string EndpointName { get; }

    public async Task<Either<HttpRequestMessage, IResult>> BuildRequest(AICallInformation callInformation, HttpContext context)
    {
        var incomingModelName = callInformation.IncomingCallDetails.IncomingModelName ?? string.Empty;

        var mappedModelName = _modelMappings.GetValueOrDefault(incomingModelName, incomingModelName);

        if (MappedModelFoundAsEmptyString(callInformation, mappedModelName))
        {
            return new Either<HttpRequestMessage, IResult>(Results.NotFound(new { message = "Unknown model" }));
        }

        try
        {
            return new Either<HttpRequestMessage, IResult>(await BuildNewRequest(context, callInformation, mappedModelName));
        }
        catch (InvalidOperationException ie)
        {
            return new Either<HttpRequestMessage, IResult>(Results.BadRequest(new { message = ie.Message }));
        }
    }

    public async Task HandleResponse(IncomingCallDetails callInformationIncomingCallDetails, HttpRequestMessage newRequest,
        HttpResponseMessage openAiResponse)
    {
        await ExtractDiagnostics(callInformationIncomingCallDetails, newRequest, openAiResponse);
    }

    public Dictionary<string, StringValues> SanitiseHeaders(HttpContext context, HttpResponseMessage openAiResponse)
    {
        return SanitiseHeaders1(context, openAiResponse);
    }
    
    protected abstract Dictionary<string, StringValues> SanitiseHeaders1(HttpContext context,
        HttpResponseMessage openAiResponse);
}