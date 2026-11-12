using System.Reflection;
using System.Runtime.CompilerServices;

namespace AiProxy.Tests.Unit.Runtime;

public sealed class RuntimeAsyncTests
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Product assemblies that declare async methods (AiProxy.Code has none).</summary>
    public static TheoryData<string> ProductAssemblies() => ["AiProxy", "AiProxy.Core", "AiProxy.Storage"];

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void Async_methods_compile_to_runtime_async_not_state_machines(string assemblyName)
    {
        var methods = Assembly.Load(assemblyName).GetTypes().SelectMany(type => type.GetMethods(AllDeclared)).ToList();

        var stateMachines = methods.Where(method => method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
            .Select(method => $"{method.DeclaringType}.{method.Name}")
            .ToList();
        Assert.Empty(stateMachines);
        Assert.Contains(methods, method => method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async));
    }
}
