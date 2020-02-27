//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2020 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ArchiSteamFarm.IPC.Integration;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ArchiSteamFarm.IPC {
	internal sealed class Startup {
		private readonly IConfiguration Configuration;

		public Startup([NotNull] IConfiguration configuration) => Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

#if NETFRAMEWORK
		public void Configure(IApplicationBuilder app, IHostingEnvironment env) {
#else
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
#endif
			if ((app == null) || (env == null)) {
				ASF.ArchiLogger.LogNullError(nameof(app) + " || " + nameof(env));

				return;
			}

			if (Debugging.IsUserDebugging) {
				app.UseDeveloperExceptionPage();
			}

			// The order of dependency injection matters, pay attention to it

			// TODO: Try to get rid of this workaround for missing PathBase feature, https://github.com/aspnet/AspNetCore/issues/5898
			PathString pathBase = Configuration.GetSection("Kestrel").GetValue<PathString>("PathBase");

			if (!string.IsNullOrEmpty(pathBase) && (pathBase != "/")) {
				app.UsePathBase(pathBase);
			}

			// Add support for proxies
			app.UseForwardedHeaders();

			// Add support for response compression
			app.UseResponseCompression();

			// Add support for websockets used in /Api/NLog
			app.UseWebSockets();

			// We're using index for URL routing in our static files so re-execute all non-API calls on /
			app.UseWhen(context => !context.Request.Path.StartsWithSegments("/Api", StringComparison.OrdinalIgnoreCase), appBuilder => appBuilder.UseStatusCodePagesWithReExecute("/"));

			// We need static files support for IPC GUI
			app.UseDefaultFiles();
			app.UseStaticFiles();

#if !NETFRAMEWORK
			app.UseRouting();
#endif

			if (!string.IsNullOrEmpty(ASF.GlobalConfig.IPCPassword)) {
				// We need ApiAuthenticationMiddleware for IPCPassword
				app.UseWhen(context => context.Request.Path.StartsWithSegments("/Api", StringComparison.OrdinalIgnoreCase), appBuilder => appBuilder.UseMiddleware<ApiAuthenticationMiddleware>());

				// We want to apply CORS policy in order to allow userscripts and other third-party integrations to communicate with ASF API
				// We apply CORS policy only with IPCPassword set as extra authentication measure
				app.UseCors();
			}

			// Add support for mapping controllers
#if NETFRAMEWORK
			app.UseMvcWithDefaultRoute();
#else
			app.UseEndpoints(endpoints => endpoints.MapControllers());
#endif

			// Use swagger for automatic API documentation generation
			app.UseSwagger();

			// Use friendly swagger UI
			app.UseSwaggerUI(
				options => {
					options.DisplayRequestDuration();
					options.EnableDeepLinking();
					options.ShowExtensions();
					options.SwaggerEndpoint("/swagger/" + SharedInfo.ASF + "/swagger.json", SharedInfo.ASF + " API");
				}
			);
		}

		public void ConfigureServices(IServiceCollection services) {
			if (services == null) {
				ASF.ArchiLogger.LogNullError(nameof(services));

				return;
			}

			// The order of dependency injection matters, pay attention to it

			// Add support for proxies
			services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

			// Add support for response compression
			services.AddResponseCompression();

			// Add CORS to allow userscripts and third-party apps
			services.AddCors(options => options.AddDefaultPolicy(policyBuilder => policyBuilder.AllowAnyOrigin()));

			// Add swagger documentation generation
			services.AddSwaggerGen(
				options => {
					options.AddSecurityDefinition(
						nameof(GlobalConfig.IPCPassword), new OpenApiSecurityScheme {
							Description = nameof(GlobalConfig.IPCPassword) + " authentication using request headers. Check " + SharedInfo.ProjectURL + "/wiki/IPC#authentication for more info.",
							In = ParameterLocation.Header,
							Name = ApiAuthenticationMiddleware.HeadersField,
							Type = SecuritySchemeType.ApiKey
						}
					);

					options.AddSecurityRequirement(
						new OpenApiSecurityRequirement {
							{
								new OpenApiSecurityScheme {
									Reference = new OpenApiReference {
										Id = nameof(GlobalConfig.IPCPassword),
										Type = ReferenceType.SecurityScheme
									}
								},

								new string[0]
							}
						}
					);

					options.EnableAnnotations(true);

					options.SwaggerDoc(
						SharedInfo.ASF, new OpenApiInfo {
							Contact = new OpenApiContact {
								Name = SharedInfo.GithubRepo,
								Url = new Uri(SharedInfo.ProjectURL)
							},

							License = new OpenApiLicense {
								Name = SharedInfo.LicenseName,
								Url = new Uri(SharedInfo.LicenseURL)
							},

							Title = SharedInfo.ASF + " API"
						}
					);

					string xmlDocumentationFile = Path.Combine(AppContext.BaseDirectory, SharedInfo.AssemblyDocumentation);

					if (File.Exists(xmlDocumentationFile)) {
						options.IncludeXmlComments(xmlDocumentationFile);
					}
				}
			);

			// Add Newtonsoft.Json support for SwaggerGen, this one must be executed after AddSwaggerGen()
			services.AddSwaggerGenNewtonsoftSupport();

			// We need MVC for /Api, but we're going to use only a small subset of all available features
#if NETFRAMEWORK
			IMvcCoreBuilder mvc = services.AddMvcCore();
#else
			IMvcBuilder mvc = services.AddControllers();
#endif

			// Add support for controllers declared in custom plugins
			HashSet<Assembly> assemblies = PluginsCore.LoadAssemblies();

			if (assemblies != null) {
				foreach (Assembly assembly in assemblies) {
					mvc.AddApplicationPart(assembly);
				}
			}

			// Use latest compatibility version for MVC
			mvc.SetCompatibilityVersion(CompatibilityVersion.Latest);

#if NETFRAMEWORK
			// Add standard formatters
			mvc.AddFormatterMappings();

			// Add API explorer for swagger
			mvc.AddApiExplorer();
#endif

#if NETFRAMEWORK
			// Add JSON formatters that will be used as default ones if no specific formatters are asked for
			mvc.AddJsonFormatters();

			mvc.AddJsonOptions(
#else
			mvc.AddNewtonsoftJson(
#endif
				options => {
					// Fix default contract resolver to use original names and not a camel case
					options.SerializerSettings.ContractResolver = new DefaultContractResolver();

					// TODO: Enable this again once ASF-ui adds support in https://github.com/JustArchiNET/ASF-ui/issues/871
					//options.SerializerSettings.Converters.Add(new StringEnumConverter());

					if (Debugging.IsUserDebugging) {
						options.SerializerSettings.Formatting = Formatting.Indented;
					}
				}
			);
		}
	}
}
