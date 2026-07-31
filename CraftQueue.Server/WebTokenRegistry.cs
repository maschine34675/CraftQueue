using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CraftQueue.Server
{
    [Injectable(InjectionType = InjectionType.Singleton)]
    public sealed class WebTokenRegistry
    {
        private readonly ConcurrentDictionary<MongoId, string> tokensBySession = new();
        private readonly ConcurrentDictionary<string, MongoId> sessionsByToken = new(StringComparer.Ordinal);

        public string HostToken { get; } = CreateToken();

        public string GetOrCreateSessionToken(MongoId sessionId)
        {
            return tokensBySession.GetOrAdd(sessionId, id =>
            {
                string token = CreateToken();
                sessionsByToken[token] = id;
                return token;
            });
        }
        public WebTokenScope? Resolve(string? token)
        {
            if (string.IsNullOrEmpty(token))
                return null;

            byte[] host = Encoding.UTF8.GetBytes(HostToken);
            byte[] supplied = Encoding.UTF8.GetBytes(token);
            if (host.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(host, supplied))
                return WebTokenScope.Host;

            return sessionsByToken.TryGetValue(token, out MongoId sessionId)
                ? WebTokenScope.ForProfile(sessionId)
                : null;
        }

        private static string CreateToken()
        {
            byte[] token = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public sealed class WebTokenScope
    {
        private WebTokenScope(MongoId? profileId)
        {
            ProfileId = profileId;
        }

        public static WebTokenScope Host { get; } = new(null);

        public static WebTokenScope ForProfile(MongoId profileId) => new(profileId);
        public MongoId? ProfileId { get; }

        public bool MayAccess(MongoId profileId)
        {
            return ProfileId == null || ProfileId.Value == profileId;
        }
    }
}
