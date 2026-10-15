namespace RSyncStageCraft.Core.Dependencies;

/// <summary>
/// Read-only installed-plugin metadata for a possible receiver-side host.
/// This is inventory information only and never implies IPC compatibility or
/// receipt-safe ownership semantics.
/// </summary>
internal readonly record struct OptionalHostMetadata(
    string DisplayName,
    string InternalName,
    bool InventoryObserved,
    bool Installed,
    string? InstalledVersion);
