﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System.Net;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// The cosmos permission response
    /// </summary>
    public class PermissionResponse : Response<PermissionProperties>
    {
        /// <summary>
        /// Create a <see cref="PermissionResponse"/> as a no-op for mock testing
        /// </summary>
        protected PermissionResponse()
            : base()
        {
        }

        /// <summary>
        /// A private constructor to ensure the factory is used to create the object.
        /// This will prevent memory leaks when handling the HttpResponseMessage
        /// </summary>
        internal PermissionResponse(
            HttpStatusCode httpStatusCode,
            CosmosHeaders headers,
            PermissionProperties permissionProperties,
            Permission permission)
        {
            this.StatusCode = httpStatusCode;
            this.CosmosHeaders = headers;
            this.Resource = permissionProperties;
            this.Permission = permission;
        }

        /// <summary>
        /// The reference to the cosmos permission. This allows additional operations on the permission
        /// or for easy access permissions
        /// </summary>
        public virtual Permission Permission { get; private set; }

        /// <inheritdoc/>
        internal override CosmosHeaders CosmosHeaders { get; }

        /// <inheritdoc/>
        public override PermissionProperties Resource { get; }

        /// <inheritdoc/>
        public override HttpStatusCode StatusCode { get; }

        /// <inheritdoc/>
        public override double RequestCharge => this.CosmosHeaders?.RequestCharge ?? 0;

        /// <inheritdoc/>
        public override string ActivityId => this.CosmosHeaders?.ActivityId;

        /// <inheritdoc/>
        public override string ETag => this.CosmosHeaders?.ETag;

        /// <inheritdoc/>
        internal override string MaxResourceQuota => this.CosmosHeaders?.GetHeaderValue<string>(HttpConstants.HttpHeaders.MaxResourceQuota);

        /// <inheritdoc/>
        internal override string CurrentResourceQuotaUsage => this.CosmosHeaders?.GetHeaderValue<string>(HttpConstants.HttpHeaders.CurrentResourceQuotaUsage);

        /// <summary>
        /// Get <see cref="Cosmos.Permission"/> implicitly from <see cref="PermissionResponse"/>
        /// </summary>
        /// <param name="response">PermissionResponse</param>
        public static implicit operator Permission(PermissionResponse response)
        {
            return response.Permission;
        }
    }
}