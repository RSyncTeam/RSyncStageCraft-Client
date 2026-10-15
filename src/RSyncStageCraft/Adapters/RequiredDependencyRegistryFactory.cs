using System.Runtime.ExceptionServices;
using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Transactionally creates the exact required read-only dependency pair.
/// A failed second construction or registration disposes both the rejected
/// adapter and every adapter whose ownership already entered the registry.
/// </summary>
internal static class RequiredDependencyRegistryFactory
{
    internal static DependencyRegistry Create(
        Func<IDependencyAdapter> createPenumbra,
        Func<IDependencyAdapter> createGlamourer)
    {
        ArgumentNullException.ThrowIfNull(createPenumbra);
        ArgumentNullException.ThrowIfNull(createGlamourer);

        var registry = new DependencyRegistry(maximumDependencies: 2);
        try
        {
            RegisterOwned(registry, createPenumbra, "Penumbra");
            RegisterOwned(registry, createGlamourer, "Glamourer");
            return registry;
        }
        catch (Exception bootstrapFailure)
        {
            try
            {
                registry.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Required dependency bootstrap and rollback both failed.",
                    bootstrapFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(bootstrapFailure).Throw();
            throw;
        }
    }

    private static void RegisterOwned(
        DependencyRegistry registry,
        Func<IDependencyAdapter> factory,
        string expectedName)
    {
        IDependencyAdapter? adapter = null;
        Exception? registrationFailure = null;
        try
        {
            adapter = factory() ?? throw new InvalidOperationException(
                $"The {expectedName} dependency factory returned null.");
            if (!string.Equals(adapter.Name, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The required dependency adapter must be named {expectedName}.");
            }

            var registration = registry.TryRegister(adapter);
            if (!registration.Registered)
            {
                throw new InvalidOperationException(
                    $"The {expectedName} dependency could not be registered: " +
                    registration.Status);
            }

            adapter = null;
            return;
        }
        catch (Exception failure)
        {
            registrationFailure = failure;
        }

        if (adapter is not null)
        {
            try
            {
                adapter.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    $"The rejected {expectedName} dependency failed to dispose.",
                    registrationFailure!,
                    cleanupFailure);
            }
        }

        ExceptionDispatchInfo.Capture(registrationFailure!).Throw();
    }
}
