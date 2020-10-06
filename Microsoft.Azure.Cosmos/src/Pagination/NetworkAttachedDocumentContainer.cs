﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Pagination
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.ChangeFeed.Pagination;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.CrossPartition;
    using Microsoft.Azure.Cosmos.Query.Core.QueryClient;
    using Microsoft.Azure.Documents;

    internal sealed class NetworkAttachedDocumentContainer : IMonadicDocumentContainer
    {
        private readonly ContainerCore container;
        private readonly CosmosQueryContext cosmosQueryContext;
        private readonly CosmosClientContext cosmosClientContext;

        public NetworkAttachedDocumentContainer(
            ContainerCore container,
            CosmosQueryContext cosmosQueryContext,
            CosmosClientContext cosmosClientContext)
        {
            this.container = container ?? throw new ArgumentNullException(nameof(container));
            this.cosmosQueryContext = cosmosQueryContext ?? throw new ArgumentNullException(nameof(cosmosQueryContext));
            this.cosmosClientContext = cosmosClientContext ?? throw new ArgumentNullException(nameof(cosmosClientContext));
        }

        public Task<TryCatch> MonadicSplitAsync(
            FeedRangeInternal feedRange,
            CancellationToken cancellationToken) => Task.FromResult(TryCatch.FromException(new NotSupportedException()));

        public async Task<TryCatch<Record>> MonadicCreateItemAsync(
            CosmosObject payload,
            CancellationToken cancellationToken)
        {
            ItemResponse<CosmosObject> tryInsertDocument = await this.container.CreateItemAsync(
                payload,
                cancellationToken: cancellationToken);
            if (tryInsertDocument.StatusCode != HttpStatusCode.Created)
            {
                return TryCatch<Record>.FromException(
                    new CosmosException(
                        message: "Failed to insert document",
                        statusCode: tryInsertDocument.StatusCode,
                        subStatusCode: default,
                        activityId: tryInsertDocument.ActivityId,
                        requestCharge: tryInsertDocument.RequestCharge));
            }

            CosmosObject insertedDocument = tryInsertDocument.Resource;
            string identifier = ((CosmosString)insertedDocument["id"]).Value;
            ResourceId resourceIdentifier = ResourceId.Parse(((CosmosString)insertedDocument["_rid"]).Value);
            long timestamp = Number64.ToLong(((CosmosNumber)insertedDocument["_ts"]).Value);

            Record record = new Record(resourceIdentifier, timestamp, identifier, insertedDocument);

            return TryCatch<Record>.FromResult(record);
        }

        public Task<TryCatch<Record>> MonadicReadItemAsync(
            CosmosElement partitionKey,
            string identifer,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<TryCatch<List<FeedRangeInternal>>> MonadicGetFeedRangesAsync(
            CancellationToken cancellationToken) => this.MonadicGetChildRangeAsync(FeedRangeEpk.FullRange, cancellationToken);

        public async Task<TryCatch<List<FeedRangeInternal>>> MonadicGetChildRangeAsync(
            FeedRangeInternal feedRange,
            CancellationToken cancellationToken)
        {
            try
            {
                ContainerProperties containerProperties = await this.cosmosClientContext.GetCachedContainerPropertiesAsync(
                    this.cosmosQueryContext.ResourceLink,
                    cancellationToken);
                List<PartitionKeyRange> overlappingRanges = await this.cosmosQueryContext.QueryClient.GetTargetPartitionKeyRangeByFeedRangeAsync(
                    this.cosmosQueryContext.ResourceLink,
                    this.cosmosQueryContext.ContainerResourceId,
                    containerProperties.PartitionKey,
                    feedRange);
                return TryCatch<List<FeedRangeInternal>>.FromResult(
                    overlappingRanges.Select(range => (FeedRangeInternal)new FeedRangePartitionKeyRange(range.Id)).ToList());
            }
            catch (Exception ex)
            {
                return TryCatch<List<FeedRangeInternal>>.FromException(ex);
            }
        }

        public Task<TryCatch<DocumentContainerPage>> MonadicReadFeedAsync(
            FeedRangeInternal feedRange,
            ResourceId resourceIdentifer,
            int pageSize,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<TryCatch<QueryPage>> MonadicQueryAsync(
            SqlQuerySpec sqlQuerySpec,
            string continuationToken,
            FeedRangeInternal feedRange,
            int pageSize,
            CancellationToken cancellationToken)
        {
            TryCatch<QueryPage> monadicQueryPage;
            switch (feedRange)
            {
                case FeedRangePartitionKey feedRangePartitionKey:
                    monadicQueryPage = await this.cosmosQueryContext.QueryClient.ExecuteItemQueryAsync(
                        this.cosmosQueryContext.ResourceLink,
                        this.cosmosQueryContext.ResourceTypeEnum,
                        this.cosmosQueryContext.OperationTypeEnum,
                        this.cosmosQueryContext.CorrelatedActivityId,
                        new QueryRequestOptions()
                        {
                            PartitionKey = feedRangePartitionKey.PartitionKey,
                        },
                        queryPageDiagnostics: default,
                        sqlQuerySpec,
                        continuationToken,
                        partitionKeyRange: default,
                        isContinuationExpected: true,
                        pageSize,
                        cancellationToken);
                    break;

                case FeedRangePartitionKeyRange feedRangePartitionKeyRange:
                    monadicQueryPage = await this.cosmosQueryContext.ExecuteQueryAsync(
                        querySpecForInit: sqlQuerySpec,
                        continuationToken: continuationToken,
                        partitionKeyRange: new PartitionKeyRangeIdentity(
                            this.cosmosQueryContext.ContainerResourceId,
                            feedRangePartitionKeyRange.PartitionKeyRangeId),
                        isContinuationExpected: this.cosmosQueryContext.IsContinuationExpected,
                        pageSize: pageSize,
                        cancellationToken: cancellationToken);
                    break;

                case FeedRangeEpk feedRangeEpk:
                    ContainerProperties containerProperties = await this.cosmosClientContext.GetCachedContainerPropertiesAsync(
                    this.cosmosQueryContext.ResourceLink,
                    cancellationToken);
                    List<PartitionKeyRange> overlappingRanges = await this.cosmosQueryContext.QueryClient.GetTargetPartitionKeyRangeByFeedRangeAsync(
                        this.cosmosQueryContext.ResourceLink,
                        this.cosmosQueryContext.ContainerResourceId,
                        containerProperties.PartitionKey,
                        feedRange);

                    if ((overlappingRanges == null) || (overlappingRanges.Count != 1))
                    {
                        // Simulate a split exception, since we don't have a partition key range id to route to.
                        CosmosException goneException = new CosmosException(
                            message: $"Epk Range: {feedRangeEpk.Range} is gone.",
                            statusCode: System.Net.HttpStatusCode.Gone,
                            subStatusCode: (int)SubStatusCodes.PartitionKeyRangeGone,
                            activityId: Guid.NewGuid().ToString(),
                            requestCharge: default);

                        return TryCatch<QueryPage>.FromException(goneException);
                    }

                    monadicQueryPage = await this.cosmosQueryContext.ExecuteQueryAsync(
                        querySpecForInit: sqlQuerySpec,
                        continuationToken: continuationToken,
                        partitionKeyRange: new PartitionKeyRangeIdentity(
                            this.cosmosQueryContext.ContainerResourceId,
                            overlappingRanges[0].Id),
                        isContinuationExpected: this.cosmosQueryContext.IsContinuationExpected,
                        pageSize: pageSize,
                        cancellationToken: cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            return monadicQueryPage;
        }

        public async Task<TryCatch<ChangeFeedPage>> MonadicChangeFeedAsync(
            ChangeFeedState state,
            FeedRangeInternal feedRange,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResponseMessage responseMessage = await this.cosmosClientContext.ProcessResourceOperationStreamAsync(
                resourceUri: this.container.LinkUri,
                resourceType: ResourceType.Document,
                operationType: OperationType.ReadFeed,
                requestOptions: default,
                cosmosContainerCore: this.container,
                requestEnricher: (request) =>
                {
                    state.Accept(ChangeFeedStateRequestMessagePopulator.Singleton, request);
                    feedRange.Accept(FeedRangeRequestMessagePopulatorVisitor.Singleton, request);

                    request.Headers.PageSize = pageSize.ToString();
                    request.Headers.Add(
                        HttpConstants.HttpHeaders.A_IM,
                        HttpConstants.A_IMHeaderValues.IncrementalFeed);
                },
                partitionKey: default,
                streamPayload: default,
                diagnosticsContext: default,
                cancellationToken: cancellationToken);

            if (!responseMessage.IsSuccessStatusCode)
            {
                CosmosException cosmosException = new CosmosException(
                    responseMessage.ErrorMessage,
                    statusCode: responseMessage.StatusCode,
                    (int)responseMessage.Headers.SubStatusCode,
                    responseMessage.Headers.ActivityId,
                    responseMessage.Headers.RequestCharge);
                cosmosException.Headers.ContinuationToken = responseMessage.Headers.ContinuationToken;

                return TryCatch<ChangeFeedPage>.FromException(cosmosException);
            }

            ChangeFeedPage changeFeedPage = new ChangeFeedPage(
                responseMessage.Content,
                responseMessage.Headers.RequestCharge,
                responseMessage.Headers.ActivityId,
                ChangeFeedState.Continuation(responseMessage.ContinuationToken));
            return TryCatch<ChangeFeedPage>.FromResult(changeFeedPage);
        }

        private sealed class ChangeFeedStateRequestMessagePopulator : IChangeFeedStateVisitor<RequestMessage>
        {
            public static readonly ChangeFeedStateRequestMessagePopulator Singleton = new ChangeFeedStateRequestMessagePopulator();

            private const string IfNoneMatchAllHeaderValue = "*";

            private static readonly DateTime StartFromBeginningTime = DateTime.MinValue.ToUniversalTime();

            private ChangeFeedStateRequestMessagePopulator()
            {
            }

            public void Visit(ChangeFeedStateBeginning changeFeedStateBeginning, RequestMessage message)
            {
                // We don't need to set any headers to start from the beginning
            }

            public void Visit(ChangeFeedStateTime changeFeedStateTime, RequestMessage message)
            {
                // Our current public contract for ChangeFeedProcessor uses DateTime.MinValue.ToUniversalTime as beginning.
                // We need to add a special case here, otherwise it would send it as normal StartTime.
                // The problem is Multi master accounts do not support StartTime header on ReadFeed, and thus,
                // it would break multi master Change Feed Processor users using Start From Beginning semantics.
                // It's also an optimization, since the backend won't have to binary search for the value.
                if (changeFeedStateTime.StartTime != ChangeFeedStateRequestMessagePopulator.StartFromBeginningTime)
                {
                    message.Headers.Add(
                        HttpConstants.HttpHeaders.IfModifiedSince,
                        changeFeedStateTime.StartTime.ToString("r", CultureInfo.InvariantCulture));
                }
            }

            public void Visit(ChangeFeedStateContinuation changeFeedStateContinuation, RequestMessage message)
            {
                // On REST level, change feed is using IfNoneMatch/ETag instead of continuation
                message.Headers.IfNoneMatch = changeFeedStateContinuation.ContinuationToken;
            }

            public void Visit(ChangeFeedStateNow changeFeedStateNow, RequestMessage message)
            {
                message.Headers.IfNoneMatch = ChangeFeedStateRequestMessagePopulator.IfNoneMatchAllHeaderValue;
            }
        }
    }
}