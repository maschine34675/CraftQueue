using CraftQueue.Server.Models;
using CraftQueue.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CraftQueue.Server
{
    [Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
    public sealed class WebServer(
        CraftQueueService craftQueueService,
        SaveServer saveServer,
        WebTokenRegistry tokenRegistry,
        ILogger<WebServer> logger) : IOnLoad, IDisposable
    {
        private const int MaximumRequestBodyBytes = 256 * 1024;
        private const string AuthCookieName = "CraftQueueAuth";
        private const string AuthHeaderName = "X-CraftQueue-Token";
        private const string ScopeItemKey = "CraftQueueScope";

        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly object recipesJsonLock = new();
        private WebApplication? app;
        private byte[]? cachedRecipesJson;
        private Task? recipePreloadTask;
        private bool disposed;
        private volatile int listeningPort;
        public ClientChannelInfo GetChannelInfo(MongoId sessionId)
        {
            return new ClientChannelInfo
            {
                Port = listeningPort,
                Token = tokenRegistry.GetOrCreateSessionToken(sessionId)
            };
        }

        public async Task OnLoad()
        {
            try
            {
                WebServerConfig config = loadConfig();
                int port = resolvePort(config);
                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);

                    if (!config.AllowLan)
                    {
                        options.Listen(IPAddress.Loopback, port);
                        return;
                    }

                    if (!IPAddress.TryParse(config.BindAddress, out IPAddress? address))
                        throw new InvalidOperationException($"Invalid CraftQueue bindAddress '{config.BindAddress}'.");
                    options.Listen(address, port);
                });

                app = builder.Build();
                app.Use(handleSecurity);
                mapRoutes(app);
                await app.StartAsync();
                listeningPort = port;

                string url = $"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(tokenRegistry.HostToken)}";
                logger.LogInformation("[CraftQueue] Web UI (host link, full access to all profiles - keep it private): {Url}", url);
                if (config.AllowLan)
                    logger.LogInformation("[CraftQueue] LAN access is enabled on {Address}:{Port}; players find their personal link in their BepInEx log.", config.BindAddress, port);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[CraftQueue] Failed to start the web server. Set a free \"webPort\" in config/config.json if the port is already taken; EFT keeps working through the SPT backend meanwhile.");
            }

            logger.LogInformation("[CraftQueue] Preparing the crafting recipe cache in the background...");
            recipePreloadTask = Task.Run(preloadRecipes);
        }

        private async Task handleSecurity(HttpContext context, Func<Task> next)
        {
            try
            {
                setSecurityHeaders(context.Response);

                if (HttpMethods.IsGet(context.Request.Method)
                    && context.Request.Path == "/"
                    && context.Request.Query.TryGetValue("token", out StringValues queryToken)
                    && tokenRegistry.Resolve(queryToken.ToString()) != null)
                {
                    context.Response.Cookies.Append(AuthCookieName, queryToken.ToString(), new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = false,
                        Path = "/",
                        MaxAge = TimeSpan.FromHours(12)
                    });
                    context.Response.Redirect("/");
                    return;
                }

                WebTokenScope? scope = resolveScope(context.Request);
                if (scope == null)
                {
                    await writeText(context, StatusCodes.Status401Unauthorized, "CraftQueue authorization required.");
                    return;
                }

                context.Items[ScopeItemKey] = scope;

                if (!isSameOriginMutation(context.Request))
                {
                    await writeText(context, StatusCodes.Status403Forbidden, "Cross-origin request rejected.");
                    return;
                }

                await next();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogDebug("[CraftQueue] A web request was aborted by the client ({Path}).", context.Request.Path);
            }
            catch (BadHttpRequestException ex) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogDebug("[CraftQueue] A web request was aborted mid-body by the client ({Path}): {Message}",
                    context.Request.Path, ex.Message);
            }
            catch (BadHttpRequestException ex)
            {
                logger.LogWarning("[CraftQueue] Rejected malformed web request: {Message}", ex.Message);
                if (!context.Response.HasStarted)
                    await writeText(context, ex.StatusCode, "Invalid request.");
            }
            catch (JsonException ex)
            {
                logger.LogWarning("[CraftQueue] Rejected invalid JSON request: {Message}", ex.Message);
                if (!context.Response.HasStarted)
                    await writeText(context, StatusCodes.Status400BadRequest, "Invalid JSON.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[CraftQueue] Web request error.");
                if (!context.Response.HasStarted)
                    await writeText(context, StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        private void mapRoutes(WebApplication webApplication)
        {
            webApplication.MapGet("/", async context =>
                await writeBytes(
                    context,
                    StatusCodes.Status200OK,
                    "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(WebUI.BuildHtml(context.Request.Cookies[AuthCookieName] ?? ""))));

            webApplication.MapGet("/api/profiles", async context =>
            {
                WebTokenScope scope = scopeOf(context);
                object[] profiles = saveServer.GetProfiles()
                    .Where(pair => scope.MayAccess(pair.Key))
                    .Select(pair => new
                    {
                        id = pair.Key.ToString(),
                        name = pair.Value.CharacterData?.PmcData?.Info?.Nickname ?? pair.Key.ToString()
                    })
                    .Cast<object>()
                    .ToArray();
                await writeJson(context, profiles);
            });

            webApplication.MapGet("/api/state", async context =>
            {
                if (!tryGetProfile(context, context.Request.Query["profile"].ToString(), out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.GetState(sessionId));
            });

            webApplication.MapGet("/api/recipes", async context =>
                await writeBytes(context, StatusCodes.Status200OK, "application/json; charset=utf-8", getRecipesJson()));

            webApplication.MapPost("/api/commands/enqueue", async context =>
            {
                ProfileRecipeRequest request = await readJson<ProfileRecipeRequest>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.CreateEnqueueCommand(sessionId, request.RecipeId));
            });

            webApplication.MapPost("/api/commands/cancel-active", async context =>
            {
                ProfileRecipeRequest request = await readJson<ProfileRecipeRequest>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.CreateCancelActiveCommand(sessionId, request.RecipeId, request.InstanceId ?? ""));
            });

            webApplication.MapPost("/api/commands/dismiss", async context =>
            {
                CommandRequest request = await readJson<CommandRequest>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.DismissCommand(sessionId, request.CommandId));
            });

            webApplication.MapPost("/api/queue/cancel", async context =>
            {
                QueueEntryRequest request = await readJson<QueueEntryRequest>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.CancelQueued(sessionId, request.QueueId));
            });

            webApplication.MapPost("/api/queue/retry", async context =>
            {
                QueueEntryRequest request = await readJson<QueueEntryRequest>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.RetryQueued(sessionId, request.QueueId));
            });

            webApplication.MapPost("/api/client/poll", async context =>
            {
                ClientPollRequestData request = await readJson<ClientPollRequestData>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.Poll(sessionId, request));
            });

            webApplication.MapPost("/api/client/ack", async context =>
            {
                QueueAckRequestData request = await readJson<QueueAckRequestData>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.AcknowledgeClaim(sessionId, request));
            });

            webApplication.MapPost("/api/client/command-result", async context =>
            {
                ClientCommandResultRequestData request = await readJson<ClientCommandResultRequestData>(context);
                if (!tryGetProfile(context, request.ProfileId, out MongoId sessionId))
                {
                    await writeText(context, StatusCodes.Status404NotFound, "Profile not found.");
                    return;
                }

                await writeJson(context, craftQueueService.CompleteClientCommand(sessionId, request));
            });

            webApplication.MapFallback(async context =>
                await writeText(context, StatusCodes.Status404NotFound, "Not found."));
        }

        private void preloadRecipes()
        {
            try
            {
                Stopwatch timer = Stopwatch.StartNew();
                Dictionary<string, List<RecipeInfo>> recipes = craftQueueService.GetRecipes();
                int count = recipes.Values.Sum(list => list.Count);
                _ = getRecipesJson();
                timer.Stop();
                logger.LogInformation("[CraftQueue] Prepared {Count} crafting recipes in {ElapsedSeconds:0.0}s.", count, timer.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[CraftQueue] Failed to prepare the crafting recipe cache at startup.");
            }
        }

        private byte[] getRecipesJson()
        {
            lock (recipesJsonLock)
            {
                cachedRecipesJson ??= JsonSerializer.SerializeToUtf8Bytes(craftQueueService.GetRecipes(), jsonOptions);
                return cachedRecipesJson;
            }
        }
        private int resolvePort(WebServerConfig config)
        {
            const int LowestUnprivilegedPort = 1024;
            if (config.WebPort >= LowestUnprivilegedPort && config.WebPort <= 65535)
                return config.WebPort;

            logger.LogWarning(
                "[CraftQueue] The configured webPort {Port} is not usable (allowed: {Min}-65535); falling back to {Default}.",
                config.WebPort,
                LowestUnprivilegedPort,
                CraftQueueProtocol.WebPort);
            return CraftQueueProtocol.WebPort;
        }

        private WebServerConfig loadConfig()
        {
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string configPath = Path.Combine(modDirectory, "config", "config.json");
            if (!File.Exists(configPath))
            {
                logger.LogWarning("[CraftQueue] Web config was not found at {ConfigPath}; using loopback-only defaults.", configPath);
                return new WebServerConfig();
            }

            try
            {
                return JsonSerializer.Deserialize<WebServerConfig>(File.ReadAllText(configPath), jsonOptions)
                    ?? new WebServerConfig();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "[CraftQueue] Could not read {ConfigPath}; using loopback-only defaults.", configPath);
                return new WebServerConfig();
            }
        }

        private bool tryGetProfile(HttpContext context, string? profileId, out MongoId sessionId)
        {
            sessionId = default;
            if (string.IsNullOrWhiteSpace(profileId) || !MongoId.IsValidMongoId(profileId))
                return false;
            sessionId = profileId;
            return saveServer.ProfileExists(sessionId) && scopeOf(context).MayAccess(sessionId);
        }

        private WebTokenScope? resolveScope(HttpRequest request)
        {
            string candidate = request.Headers[AuthHeaderName].ToString();
            if (string.IsNullOrEmpty(candidate))
                candidate = request.Cookies[AuthCookieName] ?? "";
            return tokenRegistry.Resolve(candidate);
        }

        private static WebTokenScope scopeOf(HttpContext context)
        {
            return context.Items[ScopeItemKey] as WebTokenScope ?? WebTokenScope.ForProfile(default);
        }

        private static bool isSameOriginMutation(HttpRequest request)
        {
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
                return true;
            if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
                return false;

            string origin = request.Headers.Origin.ToString();
            if (string.IsNullOrWhiteSpace(origin))
                return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri))
                return false;

            int originPort = originUri.IsDefaultPort ? (originUri.Scheme == "https" ? 443 : 80) : originUri.Port;
            int requestPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
            return string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
                && originPort == requestPort;
        }

        private static void setSecurityHeaders(HttpResponse response)
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
                "img-src 'self' https://assets.tarkov.dev data:; connect-src 'self'; " +
                "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        }

        private static async Task<T> readJson<T>(HttpContext context) where T : class
        {
            if (context.Request.ContentLength > MaximumRequestBodyBytes)
                throw new BadHttpRequestException("Request body is too large.", StatusCodes.Status413PayloadTooLarge);

            T? value = await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                jsonOptions,
                context.RequestAborted);
            return value ?? throw new BadHttpRequestException("Request body is empty.", StatusCodes.Status400BadRequest);
        }

        private static Task writeJson(HttpContext context, object value)
        {
            return writeBytes(
                context,
                StatusCodes.Status200OK,
                "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions));
        }

        private static Task writeText(HttpContext context, int statusCode, string text)
        {
            return writeBytes(context, statusCode, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
        }

        private static async Task writeBytes(HttpContext context, int statusCode, string contentType, byte[] bytes)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            if (app != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { app.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch { }
                try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { }
            }

            GC.SuppressFinalize(this);
        }

        private sealed class ProfileRecipeRequest
        {
            public string? ProfileId { get; set; }
            public string RecipeId { get; set; } = "";
            public string? InstanceId { get; set; }
        }

        private sealed class QueueEntryRequest
        {
            public string? ProfileId { get; set; }
            public string QueueId { get; set; } = "";
        }

        private sealed class CommandRequest
        {
            public string? ProfileId { get; set; }
            public string CommandId { get; set; } = "";
        }
    }
}
