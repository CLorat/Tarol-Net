namespace Tarol.StronglyTypedId.Tests.ExternalAssembly;


public interface IExternalAssemblyTestClassWithId
{

}
public class ExternalAssemblyTestClassWithId
{
    public Id<IExternalAssemblyTestClassWithId> Id { get; set; }
}
