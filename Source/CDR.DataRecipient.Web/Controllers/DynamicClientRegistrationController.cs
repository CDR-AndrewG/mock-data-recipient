﻿using CDR.DataRecipient.Repository;
using CDR.DataRecipient.Repository.SQL;
using CDR.DataRecipient.SDK;
using CDR.DataRecipient.SDK.Extensions;
using CDR.DataRecipient.SDK.Models;
using CDR.DataRecipient.SDK.Services.DataHolder;
using CDR.DataRecipient.SDK.Services.Register;
using CDR.DataRecipient.Web.Configuration;
using CDR.DataRecipient.Web.Configuration.Models;
using CDR.DataRecipient.Web.Extensions;
using CDR.DataRecipient.Web.Features;
using CDR.DataRecipient.Web.Filters;
using CDR.DataRecipient.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.Mvc;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace CDR.DataRecipient.Web.Controllers
{
    [Authorize]
    [Route("dcr")]
    public class DynamicClientRegistrationController : Controller
    {
        private readonly IConfiguration _config;
        private readonly IMetadataService _metadataService;
        private readonly ISsaService _ssaService;
        private readonly IDynamicClientRegistrationService _dcrService;
        private readonly IRegistrationsRepository _regRepository;
        private readonly IDataHoldersRepository _dhRepository;
        private readonly SDK.Services.Register.IInfosecService _regInfosecService;
        private readonly SDK.Services.DataHolder.IInfosecService _dhInfosecService;
        private readonly Common.IDataHolderDiscoveryCache _dataHolderDiscoveryCache;
        private readonly ISqlDataAccess _sqlService;
        private readonly IFeatureManager _featureManager;

        public DynamicClientRegistrationController(
            IConfiguration config,
            IMetadataService metadataService,
            ISsaService ssaService,
            IRegistrationsRepository regRepository,
            IDataHoldersRepository dhRepository,
            IDynamicClientRegistrationService dcrService,
            SDK.Services.Register.IInfosecService regInfosecService,
            SDK.Services.DataHolder.IInfosecService dhInfosecService,
            Common.IDataHolderDiscoveryCache dataHolderDiscoveryCache,
            ISqlDataAccess sqlService,
            IFeatureManager featureManager)
        {
            _config = config;
            _regRepository = regRepository;
            _dhRepository = dhRepository;
            _metadataService = metadataService;
            _ssaService = ssaService;
            _dcrService = dcrService;
            _regInfosecService = regInfosecService;
            _dhInfosecService = dhInfosecService;
            _dataHolderDiscoveryCache = dataHolderDiscoveryCache;
            _sqlService = sqlService;
            _featureManager = featureManager;
        }

        [HttpGet]
        [ServiceFilter(typeof(LogActionEntryAttribute))]
        public async Task<IActionResult> Index(string clientId = null)
        {
            var model = new DynamicClientRegistrationModel();
            if (string.IsNullOrEmpty(clientId))
            {
                SetViewModelDefaults(model);
            }
            else
            {
                var client = await _regRepository.GetRegistration(clientId);
                SetViewModel(model, client);
            }

            await PopulateFormDetail(model);
            return View(model);
        }

        [FeatureGate(nameof(FeatureFlags.AllowDynamicClientRegistration))]
        [HttpPost]
        [ServiceFilter(typeof(LogActionEntryAttribute))]
        public async Task<IActionResult> Index(DynamicClientRegistrationModel model)
        {
            if (string.IsNullOrEmpty(model.ClientId))
            {
                await Register(model);
            }
            else
            {
                await UpdateRegistration(model);
            }

            await PopulateFormDetail(model);
            return View(model);
        }

        [FeatureGate(nameof(FeatureFlags.AllowDynamicClientRegistration))]
        [HttpDelete]
        [Route("registrations/{clientId}")]
        [ServiceFilter(typeof(LogActionEntryAttribute))]
        public async Task<IActionResult> Delete(string clientId)
        {
            // Delete the registration from the data holder.
            var deleteResponse = await DeleteRegistration(clientId);

            if (deleteResponse.StatusCode.IsSuccessful())
            {
                return Ok();
            }

            var response = new
            {
                StatusCode = deleteResponse.StatusCode,
                Messages = deleteResponse.Messages,
                Payload = deleteResponse.ResponsePayload
            };

            return new JsonResult(response)
            {
                StatusCode = Convert.ToInt32(deleteResponse.StatusCode)
            };
        }

        [HttpGet]
        [Route("registrations/{clientId}")]
        [ServiceFilter(typeof(LogActionEntryAttribute))]
        public async Task<IActionResult> Get(string clientId)
        {
            var reg = await GetRegistration(clientId);
            var response = new
            {
                StatusCode = reg.StatusCode,
                Messages = reg.Messages,
                Payload = reg.ResponsePayload
            };
            return new JsonResult(response)
            {
                StatusCode = Convert.ToInt32(reg.StatusCode)
            };
        }

        private async Task Register(DynamicClientRegistrationModel model)
        {
            var sp = _config.GetSoftwareProductConfig();
            var ssa = await GetSSA(sp, model);

            // Construct the DCR request.
            var dataHolderDiscovery = await _dataHolderDiscoveryCache.GetOidcDiscoveryByBrandId(model.DataHolderBrandId);
            var registrationRequestJwt = PopulateRegistrationRequestJwt(model, sp, ssa, dataHolderDiscovery.Issuer);

            // Request DCR to the Data Holder.
            var dcrResponse = await _dcrService.Register(dataHolderDiscovery.RegistrationEndpoint, sp.ClientCertificate.X509Certificate, registrationRequestJwt);

            model.StatusCode = dcrResponse.StatusCode;
            model.Messages = $"{dcrResponse.StatusCode} - {(dcrResponse.IsSuccessful ? "Registered" : dcrResponse.Message)}";
            model.ResponsePayload = dcrResponse.Payload;

            if (dcrResponse.IsSuccessful)
            {
                var registration = dcrResponse.Data;
                registration.DataHolderBrandId = model.DataHolderBrandId;

                await _regRepository.PersistRegistration(dcrResponse.Data);
            }
        }

        private async Task<DynamicClientRegistrationModel> GetRegistration(string clientId)
        {
            var model = new DynamicClientRegistrationModel();
            var sp = _config.GetSoftwareProductConfig();
            var client = await _regRepository.GetRegistration(clientId);
            var dataHolderDiscovery = await _dataHolderDiscoveryCache.GetOidcDiscoveryByBrandId(client.DataHolderBrandId);

            var tokenResponse = await _dhInfosecService.GetAccessToken(
                dataHolderDiscovery.TokenEndpoint,
                clientId,
                sp.ClientCertificate.X509Certificate,
                sp.SigningCertificate.X509Certificate,
                scope: Constants.Scopes.CDR_DYNAMIC_CLIENT_REGISTRATION,
                redirectUri: sp.RedirectUri,
                grantType: Constants.GrantTypes.CLIENT_CREDENTIALS);

            if (!tokenResponse.IsSuccessful)
            {
                model.StatusCode = tokenResponse.StatusCode;
                model.Messages = $"{tokenResponse.StatusCode} - {tokenResponse.Message}";
                return model;
            }

            // Request DCR to the Data Holder.
            var dcrResponse = await _dcrService.GetRegistration(dataHolderDiscovery.RegistrationEndpoint, sp.ClientCertificate.X509Certificate, tokenResponse.Data.AccessToken, clientId);

            model.StatusCode = dcrResponse.StatusCode;
            model.Messages = dcrResponse.Message;
            model.ResponsePayload = dcrResponse.Payload;

            return model;
        }

        private async Task UpdateRegistration(DynamicClientRegistrationModel model)
        {
            var sp = _config.GetSoftwareProductConfig();
            var ssa = await GetSSA(sp, model);
            var dataHolderDiscovery = await _dataHolderDiscoveryCache.GetOidcDiscoveryByBrandId(model.DataHolderBrandId);

            var tokenResponse = await _dhInfosecService.GetAccessToken(
                dataHolderDiscovery.TokenEndpoint,
                model.ClientId,
                sp.ClientCertificate.X509Certificate,
                sp.SigningCertificate.X509Certificate,
                scope: Constants.Scopes.CDR_DYNAMIC_CLIENT_REGISTRATION,
                redirectUri: sp.RedirectUri,
                grantType: Constants.GrantTypes.CLIENT_CREDENTIALS);

            if (!tokenResponse.IsSuccessful)
            {
                model.StatusCode = tokenResponse.StatusCode;
                model.Messages = $"{tokenResponse.StatusCode} - {tokenResponse.Message}";
                return;
            }

            // Construct the DCR request.
            var registrationRequestJwt = PopulateRegistrationRequestJwt(model, sp, ssa, dataHolderDiscovery.Issuer);

            // Request DCR to the Data Holder.
            var dcrResponse = await _dcrService.UpdateRegistration(dataHolderDiscovery.RegistrationEndpoint, sp.ClientCertificate.X509Certificate, tokenResponse.Data.AccessToken, model.ClientId, registrationRequestJwt);

            model.StatusCode = dcrResponse.StatusCode;
            model.Messages = dcrResponse.Message;
            model.ResponsePayload = dcrResponse.Payload;

            if (dcrResponse.IsSuccessful)
            {
                var registration = dcrResponse.Data;
                registration.DataHolderBrandId = model.DataHolderBrandId;

                await _regRepository.UpdateRegistration(dcrResponse.Data);
            }
        }

        private static string PopulateRegistrationRequestJwt(DynamicClientRegistrationModel model, SoftwareProduct sp, string ssa, string audience)
        {
            var claims = new List<Claim>();
            claims.Add(new Claim("jti", Guid.NewGuid().ToString()));
            claims.Add(new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer));
            claims.Add(new Claim("token_endpoint_auth_signing_alg", model.TokenEndpointAuthSigningAlg ?? ""));
            claims.Add(new Claim("token_endpoint_auth_method", model.TokenEndpointAuthMethod ?? ""));
            claims.Add(new Claim("application_type", model.ApplicationType ?? ""));
            claims.Add(new Claim("id_token_signed_response_alg", model.IdTokenSignedResponseAlg ?? ""));
            claims.Add(new Claim("id_token_encrypted_response_alg", model.IdTokenEncryptedResponseAlg ?? ""));
            claims.Add(new Claim("id_token_encrypted_response_enc", model.IdTokenEncryptedResponseEnc ?? ""));
            claims.Add(new Claim("request_object_signing_alg", model.RequestObjectSigningAlg ?? ""));
            claims.Add(new Claim("software_statement", ssa ?? ""));

            if (!string.IsNullOrEmpty(model.RedirectUris))
            {
                if (model.RedirectUris.Contains(','))
                {
                    foreach (var redirectUri in model.RedirectUris.Split(','))
                    {
                        claims.Add(new Claim("redirect_uris", redirectUri));
                    }
                }
                else if (model.RedirectUris.Contains(' '))
                {
                    foreach (var redirectUri in model.RedirectUris.Split(' '))
                    {
                        claims.Add(new Claim("redirect_uris", redirectUri));
                    }
                }
                else
                {
                    claims.Add(new Claim("redirect_uris", model.RedirectUris));
                }
            }

            foreach (var grantType in model.GrantTypes.Split(','))
            {
                claims.Add(new Claim("grant_types", grantType));
            }

            foreach (var responseType in model.ResponseTypes.Split(','))
            {
                claims.Add(new Claim("response_types", responseType));
            }

            // algorithm to be adaptable.
            var jwt = new JwtSecurityToken(
                issuer: sp.SoftwareProductId,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new X509SigningCredentials(sp.SigningCertificate.X509Certificate, SecurityAlgorithms.RsaSsaPssSha256));

            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(jwt);
        }

        private async Task<DynamicClientRegistrationModel> DeleteRegistration(string clientId)
        {
            var model = new DynamicClientRegistrationModel();
            var client = await _regRepository.GetRegistration(clientId);

            var sp = _config.GetSoftwareProductConfig();
            var dataHolderDiscovery = await _dataHolderDiscoveryCache.GetOidcDiscoveryByBrandId(client.DataHolderBrandId);
            var tokenResponse = await _dhInfosecService.GetAccessToken(
                dataHolderDiscovery.TokenEndpoint,
                clientId,
                sp.ClientCertificate.X509Certificate,
                sp.SigningCertificate.X509Certificate,
                scope: Constants.Scopes.CDR_DYNAMIC_CLIENT_REGISTRATION,
                redirectUri: sp.RedirectUri,
                grantType: Constants.GrantTypes.CLIENT_CREDENTIALS);

            if (!tokenResponse.IsSuccessful)
            {
                model.StatusCode = tokenResponse.StatusCode;
                model.Messages = $"{tokenResponse.StatusCode} - {tokenResponse.Message}";
                return model;
            }

            // Delete client from the Data Holder.  This is an optional endpoint and may not be implemented by the Data Holder.
            var dcrResponse = await _dcrService.DeleteRegistration(dataHolderDiscovery.RegistrationEndpoint, sp.ClientCertificate.X509Certificate, tokenResponse.Data.AccessToken, clientId);

            model.StatusCode = dcrResponse.StatusCode;
            model.Messages = dcrResponse.Message;
            model.ResponsePayload = dcrResponse.Payload;

            // Delete the client from the internal repository.
            await _regRepository.DeleteRegistration(clientId);

            return model;
        }

        private async Task<string> GetSSA(SoftwareProduct sp, DynamicClientRegistrationModel model)
        {
            var reg = _config.GetRegisterConfig();

            // Get an access token from the Register.
            var tokenResponse = await _regInfosecService.GetAccessToken(reg.TokenEndpoint, sp.SoftwareProductId, sp.ClientCertificate.X509Certificate, sp.SigningCertificate.X509Certificate);

            if (!tokenResponse.IsSuccessful)
            {
                model.StatusCode = tokenResponse.StatusCode;
                model.Messages = $"{tokenResponse.StatusCode} - {tokenResponse.Message}";
                return null;
            }

            // Get an SSA from the Register.
            string xvVer = "3";
            var ssaResponse = await _ssaService.GetSoftwareStatementAssertion(reg.MtlsBaseUri, xvVer, tokenResponse.Data.AccessToken, sp.ClientCertificate.X509Certificate, sp.BrandId, sp.SoftwareProductId, model.Industry);

            if (!ssaResponse.IsSuccessful)
            {
                model.StatusCode = ssaResponse.StatusCode;
                model.Messages = $"{ssaResponse.StatusCode} - {ssaResponse.Message}";
                return null;
            }

            return ssaResponse.Data;
        }

        private async Task PopulateFormDetail(DynamicClientRegistrationModel model)
        {
            // Return any Registrations from the repository.
            model.Registrations = await _regRepository.GetRegistrations();

            var allowDynamicClientRegistration = await _featureManager.IsEnabledAsync(nameof(FeatureFlags.AllowDynamicClientRegistration));
            if (allowDynamicClientRegistration)
            {
                // Return any Data Holders from repository
                model.DataHolderBrands = (await _dhRepository.GetDataHolderBrands())
                    .OrderByMockDataHolders(allowDynamicClientRegistration)
                    .Select(d => new SelectListItem(d.BrandName, d.DataHolderBrandId))
                    .ToList();

                // Set Selected item in picker
                if (model.DataHolderBrands.Count > 0 && !string.IsNullOrEmpty(model.DataHolderBrandId))
                {
                    var selected = model.DataHolderBrands.FirstOrDefault(d => d.Value.Equals(model.DataHolderBrandId, StringComparison.OrdinalIgnoreCase));
                    if (selected != null)
                    {
                        selected.Selected = true;
                    }
                }
            }
        }

        private void SetViewModelDefaults(DynamicClientRegistrationModel model)
        {
            var sp = _config.GetSoftwareProductConfig();
            model.SoftwareProductId = sp.SoftwareProductId;
            model.RedirectUris = sp.RedirectUris;
            model.Scope = sp.Scope;
            model.TokenEndpointAuthSigningAlg = sp.DefaultSigningAlgorithm;
            model.TokenEndpointAuthMethod = "private_key_jwt";
            model.GrantTypes = "client_credentials,authorization_code,refresh_token";
            model.ResponseTypes = "code id_token";
            model.ApplicationType = "web";
            model.IdTokenSignedResponseAlg = sp.DefaultSigningAlgorithm;
            model.IdTokenEncryptedResponseAlg = "RSA-OAEP";
            model.IdTokenEncryptedResponseEnc = "A256GCM";
            model.RequestObjectSigningAlg = sp.DefaultSigningAlgorithm;
            model.Messages = "Waiting...";
        }

        private void SetViewModel(DynamicClientRegistrationModel model, Registration client)
        {
            var sp = _config.GetSoftwareProductConfig();
            model.DataHolderBrandId = client.DataHolderBrandId;
            model.SoftwareProductId = sp.SoftwareProductId;
            model.RedirectUris = sp.RedirectUris;
            model.Scope = sp.Scope;
            model.TokenEndpointAuthSigningAlg = sp.DefaultSigningAlgorithm;
            model.TokenEndpointAuthMethod = "private_key_jwt";
            model.GrantTypes = "client_credentials,authorization_code,refresh_token";
            model.ResponseTypes = "code id_token";
            model.ApplicationType = "web";
            model.IdTokenSignedResponseAlg = sp.DefaultSigningAlgorithm;
            model.IdTokenEncryptedResponseAlg = "RSA-OAEP";
            model.IdTokenEncryptedResponseEnc = "A256GCM";
            model.RequestObjectSigningAlg = sp.DefaultSigningAlgorithm;
            model.Messages = "Waiting...";
        }
    }
}