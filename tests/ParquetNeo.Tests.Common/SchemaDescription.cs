using System.Text;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests;

/// <summary>A schema as text, to compare two of them: a line to a node.</summary>
public static class SchemaDescription
{
    /// <summary>The path of each node, its repetition, its physical type and length, its annotations and its levels.</summary>
    public static string Of(ParquetSchema schema)
    {
        var text = new StringBuilder();
        Visit(schema.Root, string.Empty, text);
        return text.ToString();
    }

    private static void Visit(SchemaNode node, string indent, StringBuilder text)
    {
        text.Append(indent).Append(node.Name).Append(": ").Append(node.Repetition);
        if (node is PrimitiveNode column)
        {
            text.Append(' ').Append(column.Type);
            if (column.TypeLength is { } length)
            {
                text.Append('(').Append(length).Append(')');
            }
        }

        text.Append(" logical=").Append(node.LogicalType?.ToString() ?? "-").Append(" converted=").Append(node.ConvertedType?.ToString() ?? "-")
            .Append(" levels=").Append(node.MaxDefinitionLevel).Append('/').Append(node.MaxRepetitionLevel).AppendLine();
        if (node is GroupNode group)
        {
            foreach (var child in group.Children)
            {
                Visit(child, indent + "  ", text);
            }
        }
    }
}
