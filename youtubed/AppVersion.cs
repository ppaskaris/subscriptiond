using System.Reflection;

namespace youtubed
{
    public static class AppVersion
    {
        public static string Current =>
            typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
