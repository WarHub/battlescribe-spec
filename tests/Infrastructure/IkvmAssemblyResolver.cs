using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Resolves IKVM-generated assemblies (DataUtils, CommonsIo, etc.) that aren't in deps.json
/// but are copied to the output directory by the CopyIkvmAssemblies build target.
/// xUnit v3 runs tests in-process as an exe, so the standard .NET host doesn't probe
/// the app directory for assemblies not listed in deps.json.
/// </summary>
internal static class IkvmAssemblyResolver
{
    [ModuleInitializer]
    internal static void Register()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(path))
            {
                return context.LoadFromAssemblyPath(path);
            }

            return null;
        };
    }
}
