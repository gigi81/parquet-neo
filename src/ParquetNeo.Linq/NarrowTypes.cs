using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using ParquetNeo.DataModel;

namespace ParquetNeo.Linq;

/// <summary>
/// Makes classes that have only some of the properties of another, so that a query that uses only some of the columns of a row type
/// reads only those: the serializer reads the columns the row type it is given has properties for. A property keeps its type and
/// the name of its column.
/// </summary>
internal static class NarrowTypes
{
    private static readonly ConcurrentDictionary<string, Type> Made = new(StringComparer.Ordinal);
    private static readonly Lazy<ModuleBuilder> Module = new(() =>
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ParquetNeo.Linq.Narrow"), AssemblyBuilderAccess.Run).DefineDynamicModule("Narrow"));

    private static int _counter;

    /// <summary>A class with a property for each of those, of the same name, type and column name.</summary>
    public static Type Get(IReadOnlyList<PropertyInfo> properties)
    {
        string key = string.Join('|', properties.Select(p => $"{p.Name}:{p.PropertyType.AssemblyQualifiedName}:{ColumnReference.ColumnName(p)}"));
        return Made.GetOrAdd(key, _ => Create(properties));
    }

    private static Type Create(IReadOnlyList<PropertyInfo> properties)
    {
        lock (Module)
        {
            var type = Module.Value.DefineType($"Narrow{Interlocked.Increment(ref _counter)}", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            foreach (var source in properties)
            {
                DefineProperty(type, source);
            }

            return type.CreateType();
        }
    }

    private static void DefineProperty(TypeBuilder type, PropertyInfo source)
    {
        var field = type.DefineField($"_{source.Name}", source.PropertyType, FieldAttributes.Private);
        var property = type.DefineProperty(source.Name, PropertyAttributes.None, source.PropertyType, null);
        const MethodAttributes Accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        var getter = type.DefineMethod($"get_{source.Name}", Accessor, source.PropertyType, Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);

        var setter = type.DefineMethod($"set_{source.Name}", Accessor, null, [source.PropertyType]);
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);

        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        // The name of the column, when it is not the name of the property.
        if (source.GetCustomAttribute<ParquetColumnAttribute>()?.Name is { } name)
        {
            var constructor = typeof(ParquetColumnAttribute).GetConstructor([typeof(string)])!;
            property.SetCustomAttribute(new CustomAttributeBuilder(constructor, [name]));
        }
    }
}
