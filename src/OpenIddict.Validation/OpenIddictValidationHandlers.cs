﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.Collections.Immutable;
using System.ComponentModel;
using OpenIddict.Extensions;

namespace OpenIddict.Validation;

[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class OpenIddictValidationHandlers
{
    public static ImmutableArray<OpenIddictValidationHandlerDescriptor> DefaultHandlers { get; } = ImmutableArray.Create(
        /*
         * Authentication processing:
         */
        ResolveServerConfiguration.Descriptor,
        EvaluateValidatedTokens.Descriptor,
        ValidateRequiredTokens.Descriptor,
        ValidateAccessToken.Descriptor,

        /*
         * Challenge processing:
         */
        AttachDefaultChallengeError.Descriptor,
        AttachCustomChallengeParameters.Descriptor,

        /*
         * Error processing:
         */
        AttachErrorParameters.Descriptor,
        AttachCustomErrorParameters.Descriptor)

        .AddRange(Discovery.DefaultHandlers)
        .AddRange(Introspection.DefaultHandlers)
        .AddRange(Protection.DefaultHandlers);

    /// <summary>
    /// Contains the logic responsible for resolving the server configuration.
    /// </summary>
    public sealed class ResolveServerConfiguration : IOpenIddictValidationHandler<ProcessAuthenticationContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .UseSingletonHandler<ResolveServerConfiguration>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Configuration ??= await context.Options.ConfigurationManager
                .GetConfigurationAsync(context.CancellationToken)
                .WaitAsync(context.CancellationToken) ??
                throw new InvalidOperationException(SR.GetResourceString(SR.ID0140));
        }
    }

    /// <summary>
    /// Contains the logic responsible for selecting the token types that should be validated.
    /// </summary>
    public sealed class EvaluateValidatedTokens : IOpenIddictValidationHandler<ProcessAuthenticationContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .UseSingletonHandler<EvaluateValidatedTokens>()
                .SetOrder(ResolveServerConfiguration.Descriptor.Order + 1_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            (context.ExtractAccessToken,
             context.RequireAccessToken,
             context.ValidateAccessToken,
             context.RejectAccessToken) = context.EndpointType switch
            {
                // The validation handler is responsible for validating access tokens for endpoints
                // it doesn't manage (typically, API endpoints using token authentication).
                OpenIddictValidationEndpointType.Unknown => (true, true, true, true),

                _ => (false, false, false, false)
            };

            // Note: unlike the equivalent event in the server stack, authentication can be triggered for
            // arbitrary requests (typically, API endpoints that are not owned by the validation stack).
            // As such, the token is not directly resolved from the request, that may be null at this stage.
            // Instead, the token is expected to be populated by one or multiple handlers provided by the host.

            return default;
        }
    }

    /// <summary>
    /// Contains the logic responsible for rejecting authentication demands that lack required tokens.
    /// </summary>
    public sealed class ValidateRequiredTokens : IOpenIddictValidationHandler<ProcessAuthenticationContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .UseSingletonHandler<ValidateRequiredTokens>()
                // Note: this handler is registered with a high gap to allow handlers
                // that do token extraction to be executed before this handler runs.
                .SetOrder(EvaluateValidatedTokens.Descriptor.Order + 50_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.RequireAccessToken && string.IsNullOrEmpty(context.AccessToken))
            {
                context.Reject(
                    error: Errors.MissingToken,
                    description: SR.GetResourceString(SR.ID2000),
                    uri: SR.FormatID8000(SR.ID2000));

                return default;
            }

            return default;
        }
    }

    /// <summary>
    /// Contains the logic responsible for ensuring a token was correctly resolved from the context.
    /// </summary>
    public sealed class ValidateAccessToken : IOpenIddictValidationHandler<ProcessAuthenticationContext>
    {
        private readonly IOpenIddictValidationDispatcher _dispatcher;

        public ValidateAccessToken(IOpenIddictValidationDispatcher dispatcher)
            => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .AddFilter<RequireAccessTokenValidated>()
                .UseScopedHandler<ValidateAccessToken>()
                .SetOrder(ValidateRequiredTokens.Descriptor.Order + 1_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.AccessTokenPrincipal is not null || string.IsNullOrEmpty(context.AccessToken))
            {
                return;
            }

            var notification = new ValidateTokenContext(context.Transaction)
            {
                Token = context.AccessToken,
                ValidTokenTypes = { TokenTypeHints.AccessToken }
            };

            await _dispatcher.DispatchAsync(notification);

            if (notification.IsRequestHandled)
            {
                context.HandleRequest();
                return;
            }

            else if (notification.IsRequestSkipped)
            {
                context.SkipRequest();
                return;
            }

            else if (notification.IsRejected)
            {
                if (context.RejectAccessToken)
                {
                    context.Reject(
                        error: notification.Error ?? Errors.InvalidRequest,
                        description: notification.ErrorDescription,
                        uri: notification.ErrorUri);
                    return;
                }

                return;
            }

            context.AccessTokenPrincipal = notification.Principal;
        }
    }

    /// <summary>
    /// Contains the logic responsible for ensuring that the challenge response contains an appropriate error.
    /// </summary>
    public sealed class AttachDefaultChallengeError : IOpenIddictValidationHandler<ProcessChallengeContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessChallengeContext>()
                .UseSingletonHandler<AttachDefaultChallengeError>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessChallengeContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Try to retrieve the authentication context from the validation transaction and use
            // the error details returned during the authentication processing, if available.
            // If no error is attached to the authentication context, this likely means that
            // the request was rejected very early without even checking the access token or was
            // rejected due to a lack of permission. In this case, return an insufficient_access error
            // to inform the client that the user is not allowed to perform the requested action.

            var notification = context.Transaction.GetProperty<ProcessAuthenticationContext>(
                typeof(ProcessAuthenticationContext).FullName!);

            context.Response.Error ??= notification?.Error ?? Errors.InsufficientAccess;
            context.Response.ErrorDescription ??= notification?.ErrorDescription ?? SR.GetResourceString(SR.ID2095);
            context.Response.ErrorUri ??= notification?.ErrorUri ?? SR.FormatID8000(SR.ID2095);

            return default;
        }
    }

    /// <summary>
    /// Contains the logic responsible for attaching the parameters
    /// populated from user-defined handlers to the sign-out response.
    /// </summary>
    public sealed class AttachCustomChallengeParameters : IOpenIddictValidationHandler<ProcessChallengeContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessChallengeContext>()
                .UseSingletonHandler<AttachCustomChallengeParameters>()
                .SetOrder(100_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessChallengeContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Parameters.Count > 0)
            {
                foreach (var parameter in context.Parameters)
                {
                    context.Response.SetParameter(parameter.Key, parameter.Value);
                }
            }

            return default;
        }
    }

    /// <summary>
    /// Contains the logic responsible for attaching the appropriate parameters to the error response.
    /// </summary>
    public sealed class AttachErrorParameters : IOpenIddictValidationHandler<ProcessErrorContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessErrorContext>()
                .UseSingletonHandler<AttachErrorParameters>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessErrorContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Response.Error = context.Error;
            context.Response.ErrorDescription = context.ErrorDescription;
            context.Response.ErrorUri = context.ErrorUri;

            return default;
        }
    }

    /// <summary>
    /// Contains the logic responsible for attaching the parameters
    /// populated from user-defined handlers to the error response.
    /// </summary>
    public sealed class AttachCustomErrorParameters : IOpenIddictValidationHandler<ProcessErrorContext>
    {
        /// <summary>
        /// Gets the default descriptor definition assigned to this handler.
        /// </summary>
        public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
            = OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessErrorContext>()
                .UseSingletonHandler<AttachCustomErrorParameters>()
                .SetOrder(100_000)
                .SetType(OpenIddictValidationHandlerType.BuiltIn)
                .Build();

        /// <inheritdoc/>
        public ValueTask HandleAsync(ProcessErrorContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Parameters.Count > 0)
            {
                foreach (var parameter in context.Parameters)
                {
                    context.Response.SetParameter(parameter.Key, parameter.Value);
                }
            }

            return default;
        }
    }
}
