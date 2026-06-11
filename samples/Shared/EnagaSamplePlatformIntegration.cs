using Enaga.Hosting;

#if HOST_WINDOWS
using Enaga.Platforms.Windows;
#elif HOST_MACOS
using Enaga.Platforms.Mac;
#endif

namespace Csmx.Samples;

internal static class EnagaSamplePlatformIntegration
{
    public static INativeWindowPlatformIntegration Create() =>
#if HOST_WINDOWS
        new WindowsNativeWindowPlatformIntegration();
#elif HOST_MACOS
        new MacNativeWindowPlatformIntegration();
#else
        new DefaultNativeWindowPlatformIntegration();
#endif
}
