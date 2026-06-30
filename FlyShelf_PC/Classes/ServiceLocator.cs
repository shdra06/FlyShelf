// ---------------------------------------------------------------
// ServiceLocator — Transitional DI Bridge
// 
// Provides a static accessor for services registered via
// Microsoft.Extensions.DependencyInjection. This is a BRIDGE
// pattern for incremental migration from static singletons:
//
// Migration path:
//   1. Register services here during App.OnStartup
//   2. Replace static Instance access with ServiceLocator.Get<T>()
//   3. Eventually inject via constructors (final step)
//
// See: https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection
// ---------------------------------------------------------------
using System;
using Microsoft.Extensions.DependencyInjection;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Service locator bridge for incremental DI migration.
    /// 
    /// Current architecture uses static singletons (e.g., ThemeManager.Instance).
    /// This class enables gradual migration without a big-bang rewrite.
    /// </summary>
    public static class ServiceLocator
    {
        private static IServiceProvider? _services;

        /// <summary>
        /// Configures the service provider. Call once during app startup.
        /// </summary>
        public static void Configure(IServiceProvider provider)
        {
            _services = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>
        /// Gets a required service. Throws InvalidOperationException if not registered.
        /// </summary>
        public static T Get<T>() where T : class
        {
            if (_services == null)
                throw new InvalidOperationException(
                    "ServiceLocator not configured. Call Configure() during app startup.");
            return _services.GetRequiredService<T>();
        }

        /// <summary>
        /// Gets an optional service. Returns null if not registered.
        /// </summary>
        public static T? GetOptional<T>() where T : class
        {
            return _services?.GetService<T>();
        }

        /// <summary>
        /// Returns true if the service provider has been configured.
        /// </summary>
        public static bool IsConfigured => _services != null;
    }
}
