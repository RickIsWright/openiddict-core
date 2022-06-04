﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Microsoft.AspNet.Identity;
using Microsoft.Owin.Security;
using OpenIddict.Client.Owin;
using static OpenIddict.Client.Owin.OpenIddictClientOwinConstants;

namespace OpenIddict.Sandbox.AspNet.Server.Controllers
{
    public class AuthenticationController : Controller
    {
        // Note: this controller uses the same callback action for all providers
        // but for users who prefer using a different action per provider,
        // the following action can be split into separate actions.
        [AcceptVerbs("GET", "POST"), Route("~/signin-{provider}")]
        public async Task<ActionResult> Callback()
        {
            var context = HttpContext.GetOwinContext();

            // Retrieve the authorization data validated by OpenIddict as part of the callback handling.
            var result = await context.Authentication.AuthenticateAsync(OpenIddictClientOwinDefaults.AuthenticationType);

            // Multiple strategies exist to handle OAuth 2.0/OpenID Connect callbacks, each with their pros and cons:
            //
            //   * Directly using the tokens to perform the necessary action(s) on behalf of the user, which is suitable
            //     for applications that don't need a long-term access to the user's resources or don't want to store
            //     access/refresh tokens in a database or in an authentication cookie (which has security implications).
            //     It is also suitable for applications that don't need to authenticate users but only need to perform
            //     action(s) on their behalf by making API calls using the access token returned by the remote server.
            //
            //   * Storing the external claims/tokens in a database (and optionally keeping the essential claims in an
            //     authentication cookie so that cookie size limits are not hit). For the applications that use ASP.NET
            //     Core Identity, the UserManager.SetAuthenticationTokenAsync() API can be used to store external tokens.
            //
            //     Note: in this case, it's recommended to use column encryption to protect the tokens in the database.
            //
            //   * Storing the external claims/tokens in an authentication cookie, which doesn't require having
            //     a user database but may be affected by the cookie size limits enforced by most browser vendors
            //     (e.g Safari for macOS and Safari for iOS/iPadOS enforce a per-domain 4KB limit for all cookies).
            //
            //     Note: this is the approach used here, but the external claims are first filtered to only persist
            //     a few claims like the user identifier. The same approach is used to store the access/refresh tokens.

            // Important: if the remote server doesn't support OpenID Connect and doesn't expose a userinfo endpoint,
            // result.Principal.Identity will represent an unauthenticated identity and won't contain any claim.
            //
            // Such identities cannot be used as-is to build an authentication cookie in ASP.NET (as the
            // antiforgery stack requires at least a name claim to bind CSRF cookies to the user's identity) but
            // the access/refresh tokens can be retrieved using result.Properties.GetTokens() to make API calls.
            if (result.Identity is not ClaimsIdentity { IsAuthenticated: true })
            {
                throw new InvalidOperationException("The external authorization data cannot be used for authentication.");
            }

            // Build an identity based on the external claims and that will be used to create the authentication cookie.
            //
            // By default, all claims extracted during the authorization dance are available. The claims collection stored
            // in the cookie can be filtered out or mapped to different names depending the claim name or its issuer.
            var claims = new List<Claim>(result.Identity.Claims
                .Select(claim => claim switch
                {
                    // Note: when using external authentication providers with ASP.NET Core Identity,
                    // the "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier" claim
                    // - which is not configurable in Identity - MUST be used to store the user identifier.
                    { Type: "id", Issuer: "https://github.com/" }
                        => new Claim(ClaimTypes.NameIdentifier, claim.Value, claim.ValueType, claim.Issuer),

                    _ => claim
                })
                .Where(claim => claim switch
                {
                    // Preserve the "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier" claim.
                    { Type: ClaimTypes.NameIdentifier } => true,

                    // Applications that use multiple client registrations can filter claims based on the issuer.
                    { Type: "bio", Issuer: "https://github.com/" } => true,

                    // Don't preserve the other claims.
                    _ => false
                }));

            // The antiforgery components require both the nameidentifier and identityprovider claims
            // so the latter is manually added using the issuer identity resolved from the remote server.
            claims.Add(new Claim("http://schemas.microsoft.com/accesscontrolservice/2010/07/claims/identityprovider", claims[0].Issuer));

            var identity = new ClaimsIdentity(claims,
                authenticationType: DefaultAuthenticationTypes.ExternalCookie,
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            var properties = new AuthenticationProperties
            {
                RedirectUri = result.Properties.RedirectUri
            };

            // If needed, the tokens returned by the authorization server can be stored in the authentication cookie.
            properties.Dictionary[Tokens.BackchannelAccessToken] = GetProperty(result.Properties, Tokens.BackchannelAccessToken);
            properties.Dictionary[Tokens.RefreshToken] = GetProperty(result.Properties, Tokens.RefreshToken);

            context.Authentication.SignIn(properties, identity);
            return Redirect(properties.RedirectUri);

            static string GetProperty(AuthenticationProperties properties, string name)
                => properties.Dictionary.TryGetValue(name, out var value) ? value : string.Empty;
        }
    }
}
