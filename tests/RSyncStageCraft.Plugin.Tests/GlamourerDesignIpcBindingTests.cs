#nullable enable
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using RSyncStageCraft.Adapters;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The "Make a Copy" adapter binds exactly the three approved Glamourer
/// subscribers. Read from the plugin assembly's
/// metadata, so no Dalamud or Glamourer type is loaded.
/// </summary>
public sealed class GlamourerDesignIpcBindingTests
{
    [Fact]
    public void TheCopyAdapterHoldsOnlyTheThreeApprovedGlamourerSubscribers()
    {
        using var stream = File.OpenRead(typeof(DalamudGlamourerDesignIpc).Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var adapter = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Name) == "DalamudGlamourerDesignIpc" &&
                metadata.GetString(type.Namespace) == "RSyncStageCraft.Adapters");
        var provider = new NameProvider();
        var fields = adapter.GetFields()
            .Select(handle => metadata.GetFieldDefinition(handle).DecodeSignature(provider, null))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "Glamourer.Api.IpcSubscribers.AddDesign",
            "Glamourer.Api.IpcSubscribers.ApiVersion",
            "Glamourer.Api.IpcSubscribers.GetDesignListExtended",
        }, fields);
    }

    /// <summary>Type names from signatures, without resolving any assembly.</summary>
    private sealed class NameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
