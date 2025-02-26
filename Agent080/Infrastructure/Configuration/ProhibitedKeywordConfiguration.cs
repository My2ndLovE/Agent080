using Microsoft.Extensions.Configuration;

namespace Agent080.Infrastructure.Configuration
{
    /// <summary>
    /// Configuration for prohibited keywords
    /// </summary>
    public class ProhibitedKeywordConfiguration
    {
        public HashSet<string> Keywords { get; }

        public ProhibitedKeywordConfiguration(IConfiguration configuration)
        {
            // Try to get keywords from configuration first
            var configuredKeywords = configuration.GetSection("ProhibitedKeywords")
                .Get<string[]>();

            // Fall back to default keywords if none configured
            var defaultKeywords = new[]
            {
                //Username
                "@fdf77788",
                "@fdf7",
                "fdf77788",
                "@uiu8587",
                "uiu8587",
                "uiu85",
                "admiralblack2see",
                //Chinese Traditional
                "幼幼",
                "蘿莉",
                "呦呦",
                "喲呦",
                "喲喲喲",
                "澀澀",
                "蘿莉",
                "幼女",
                "歡迎購票上車",
                "私我進群",
                "私我用戶名進群",
                "各類精品",
                //Chinese Simplified
                "幼幼",
                "萝莉",
                "呦呦",
                "哟呦",
                "哟哟",
                "涩涩",
                "萝莉",
                "幼女",
                "欢迎购票上车",
                "私我进群",
                "私我用户名进群",
                "各类精品"
            };

            Keywords = new HashSet<string>(
                configuredKeywords ?? defaultKeywords,
                StringComparer.OrdinalIgnoreCase
            );
        }
    }
}