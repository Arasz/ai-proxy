using System.Reflection;
using System.Reflection.Emit;
using AiProxy.Mcp;

namespace AiProxy.Tests.Unit;

/// <summary>ADR 0006: the MCP server, CLI and HTTP client reach the proxy only through its API, never through its storage.</summary>
public sealed class ClientBoundaryTests
{
    private static readonly string[] ClientNamespaces = ["AiProxy.Mcp", "AiProxy.Cli", "AiProxy.Client"];
    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static code => code.Value);

    [Fact]
    public void No_client_type_touches_storage_or_sqlite_even_inside_a_method_body()
    {
        var assembly = typeof(RetrievalTools).Assembly;
        var clientTypes = assembly.GetTypes().Where(IsClientType).ToArray();

        var violations = clientTypes.SelectMany(Violations).Distinct().Order().ToArray();

        Assert.NotEmpty(clientTypes);
        Assert.Empty(violations);
    }

    private static bool IsClientType(Type type)
    {
        var root = type;
        while (root.DeclaringType is not null)
        {
            root = root.DeclaringType;
        }

        return ClientNamespaces.Any(name => root.Namespace == name || root.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) == true);
    }

    private static IEnumerable<string> Violations(Type owner)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var used in TypesUsedBy(owner, all).SelectMany(Flatten).Where(IsForbidden))
        {
            yield return $"{owner.FullName} uses {used.FullName}";
        }
    }

    private static IEnumerable<Type> TypesUsedBy(Type owner, BindingFlags all)
    {
        if (owner.BaseType is not null)
        {
            yield return owner.BaseType;
        }

        foreach (var implemented in owner.GetInterfaces())
        {
            yield return implemented;
        }

        foreach (var field in owner.GetFields(all))
        {
            yield return field.FieldType;
        }

        foreach (var method in owner.GetConstructors(all).Cast<MethodBase>().Concat(owner.GetMethods(all)))
        {
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            if (method is MethodInfo { ReturnType: var returned })
            {
                yield return returned;
            }

            var body = method.GetMethodBody();
            if (body is null)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                yield return local.LocalType;
            }

            foreach (var member in MembersInBody(method, body.GetILAsByteArray()!, owner))
            {
                if (member.DeclaringType is not null)
                {
                    yield return member.DeclaringType;
                }

                switch (member)
                {
                    case Type type:
                        yield return type;
                        break;
                    case FieldInfo field:
                        yield return field.FieldType;
                        break;
                    case MethodInfo method2:
                        yield return method2.ReturnType;
                        foreach (var parameter in method2.GetParameters())
                        {
                            yield return parameter.ParameterType;
                        }

                        break;
                    case MethodBase constructor:
                        foreach (var parameter in constructor.GetParameters())
                        {
                            yield return parameter.ParameterType;
                        }

                        break;
                }
            }
        }
    }

    private static IEnumerable<MemberInfo> MembersInBody(MethodBase method, byte[] il, Type owner)
    {
        var typeArguments = owner.IsGenericTypeDefinition ? owner.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;
        var module = method.Module;
        var position = 0;
        while (position < il.Length)
        {
            var code = il[position] == 0xFE ? OpCodes[(short)(0xFE00 | il[position + 1])] : OpCodes[il[position]];
            position += code.Size;
            switch (code.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineField or OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, position);
                    yield return module.ResolveMember(token, typeArguments, methodArguments)!;
                    position += 4;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (BitConverter.ToInt32(il, position) * 4);
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    position += 8;
                    break;
                default:
                    position += 4;
                    break;
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
            {
                yield return inner;
            }
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var inner in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return inner;
            }
        }
    }

    private static bool IsForbidden(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "";
        return assemblyName == "AiProxy.Storage"
               || assemblyName.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
               || assemblyName.StartsWith("SQLitePCLRaw", StringComparison.Ordinal)
               || type.Namespace?.StartsWith("AiProxy.Storage", StringComparison.Ordinal) == true;
    }
}
