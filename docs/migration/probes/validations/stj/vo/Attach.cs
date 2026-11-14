namespace Lib;
// What the generator would emit for every JsonSerializerContext in an assembly that declares value objects.
partial class LibJson
{
    static LibJson() => Default.Options.TypeInfoResolverChain.Insert(0, ValueObjectResolver.Instance);
}
